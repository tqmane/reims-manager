#pragma once
// Included after packet primitives. RFC 8200 / RFC 4861 wire fields only.
namespace reims {
using ip6 = std::array<uint8_t,16>;
inline ip6 read_ip6(bytes b,size_t at){ip6 ip{};std::copy_n(b.begin()+at,16,ip.begin());return ip;}
inline bool unspecified(const ip6& ip){return ip==ip6{};}
inline bool unicast6(const ip6& ip){
    const ip6 loopback{0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1};
    if(unspecified(ip) || ip==loopback || ip[0]==0xff)return false;
    return !(std::all_of(ip.begin(),ip.begin()+10,[](auto b){return b==0;}) && ip[10]==0xff && ip[11]==0xff);
}
inline bool linklocal6(const ip6& ip){return ip[0]==0xfe && (ip[1]&0xc0)==0x80;}
struct layout6 {
    size_t end{},upper{},options{};uint8_t next{},nd{};
    bool fragment{},noninitial{},authenticated{},routed{};
};
inline uint32_t add_checksum(bytes b,uint32_t sum=0){
    for(size_t i=0;i<b.size();i+=2)sum+=(uint32_t(b[i])<<8)|(i+1<b.size()?b[i+1]:0);
    return sum;
}
inline uint16_t icmp6_checksum(bytes b,const layout6& l){
    uint32_t sum=add_checksum(b.subspan(22,32));
    const auto length=l.end-l.upper;
    sum+=uint32_t(length>>16)+uint32_t(length&0xffff)+58;
    sum=add_checksum(b.subspan(l.upper,length),sum);
    while(sum>>16)sum=(sum&0xffff)+(sum>>16);
    return uint16_t(~sum);
}
inline bool solicited_node(const ip6& destination,const ip6& target){
    const ip6 prefix{0xff,2,0,0,0,0,0,0,0,0,0,1,0xff,0,0,0};
    return std::equal(destination.begin(),destination.begin()+13,prefix.begin()) &&
        std::equal(destination.begin()+13,destination.end(),target.begin()+13);
}
inline bool inspect6(bytes b,layout6& l){
    l={};
    if(b.size()<54 || b.size()>1514 || be16(b,12)!=0x86dd || (b[14]>>4)!=6)return false;
    l.end=54+be16(b,18);l.upper=54;l.next=b[20];
    if(l.end>b.size())return false;
    while(l.next==0 || l.next==43 || l.next==60 || l.next==51 || l.next==44){
        if(l.end-l.upper<8)return false;
        const auto at=l.upper;const auto kind=l.next;size_t length{};
        if(kind==0 && at!=54)return false;
        if(kind==44){
            if(l.fragment || b[at+1]!=0)return false;
            const auto flags=be16(b,at+2);const auto offset=flags&0xfff8;
            const auto data=l.end-at-8;
            if((flags&6) || !data || ((flags&1) && data%8) || at-54+offset+data>65535)return false;
            l.fragment=true;length=8;l.noninitial=offset!=0;
        }else{
            length=kind==51?(size_t(b[at+1])+2)*4:(size_t(b[at+1])+1)*8;
            if(length<8 || length>l.end-at || (kind==51 && length<12))return false;
            if(kind==51)l.authenticated=true;
            if(kind==43 && b[at+3]!=0)l.routed=true;
        }
        l.next=b[at];l.upper+=length;
        if(l.noninitial)return true; // Fragment bytes are not another transport header.
    }
    if(l.next!=58)return true; // TCP/UDP/ESP/unknown payload stays byte-exact.
    if(l.end-l.upper<4)return false;
    const auto type=b[l.upper];
    if(type<133 || type>137)return true;
    // ND carrying any Fragment header is forbidden by RFC 6980. AH-protected
    // LLA options cannot be rewritten without breaking its authentication.
    if(l.fragment || l.authenticated || l.routed || b[21]!=255 || b[l.upper+1]!=0)return false;
    const size_t fixed=type==133?8:type==134?16:type==137?40:24;
    if(l.end-l.upper<fixed || icmp6_checksum(b,l)!=0)return false;
    const auto source=read_ip6(b,22),destination=read_ip6(b,38);
    if(type==134 || type==137){if(!linklocal6(source))return false;}
    if(type==136 && (!unicast6(source) || (destination[0]==0xff && (b[l.upper+4]&0x40))))return false;
    if(type>=135){
        const auto target=read_ip6(b,l.upper+8);
        if(!unicast6(target))return false;
        if(type==135 && unspecified(source) && !solicited_node(destination,target))return false;
        if(type==137 && !unicast6(read_ip6(b,l.upper+24)))return false;
    }
    l.nd=type;l.options=l.upper+fixed;
    for(size_t at=l.options;at<l.end;){
        if(l.end-at<2)return false;
        const size_t length=size_t(b[at+1])*8;
        if(!length || length>l.end-at)return false;
        if((b[at]==1 || b[at]==2) && length!=8)return false;
        if(b[at]==1 && unspecified(source))return false;
        at+=length;
    }
    return !unspecified(source) || type==133 || type==135;
}
class translator6 {
    mac wifi_,guest_;
    std::map<ip6,uint64_t> addresses_;uint64_t learned_{};
    void learn(const ip6& ip){
        if(!unicast6(ip))return;
        if(addresses_.size()>=256 && !addresses_.count(ip)){
            const auto oldest=std::min_element(addresses_.begin(),addresses_.end(),[](const auto& a,const auto& b){return a.second<b.second;});
            addresses_.erase(oldest);
        }
        addresses_[ip]=++learned_;
    }
public:
    translator6(mac wifi,mac guest):wifi_(wifi),guest_(guest){}
    bool translate(bytes b,bool from_tap){
        layout6 l;if(!inspect6(b,l))return false;
        const auto source=read_ip6(b,22),destination=read_ip6(b,38);
        // RFC 3590: MLD reports/Done may precede assignment of a link-local
        // address. Preserve their checksums and never learn :: as an address.
        const bool mld=!l.fragment && l.next==58 && l.end-l.upper>=8 && b[21]==1 &&
            (b[l.upper]==131 || b[l.upper]==132 || b[l.upper]==143) && icmp6_checksum(b,l)==0;
        if(!unicast6(source) && !(unspecified(source) && (l.nd==133 || l.nd==135 || mld)))return false;
        if(!unicast6(destination) && destination[0]!=0xff)return false;
        if(from_tap){
            if(read_mac(b,6)!=guest_)return false;
            // A tentative DAD target is never learned as assigned to this guest.
            learn(source);
            if(l.nd==136)learn(read_ip6(b,l.upper+8));
            if(l.nd){
                bool changed=false;
                for(size_t at=l.options;at<l.end;at+=size_t(b[at+1])*8){
                    if((b[at]==1 || b[at]==2) && read_mac(b,at+2)==guest_){write_mac(b,at+2,wifi_);changed=true;}
                }
                if(changed){put16(b,l.upper+2,0);put16(b,l.upper+2,icmp6_checksum(b,l));}
            }
            write_mac(b,6,wifi_);return true;
        }
        if(read_mac(b,6)==wifi_)return false;
        // Peer/router LLA options name that peer, not the destination guest.
        // Preserve them. Only the destination Ethernet address is translated.
        if(destination[0]==0xff){
            const mac multicast{0x33,0x33,destination[12],destination[13],destination[14],destination[15]};
            write_mac(b,0,multicast);return true;
        }
        const auto target=addresses_.find(destination);
        if(target==addresses_.end())return false;
        target->second=++learned_;write_mac(b,0,guest_);return true;
    }
};
}
