#pragma once
#include <array>
#include <cstdint>
#include <algorithm>
#include <map>
#include <gsl/span>

// Authored bounded MAC translation, following the MIT ndisapi
// ethernet_bridge sample. Unsupported Ethernet protocols never leave this relay.
namespace reims {
using mac = std::array<uint8_t, 6>;
using bytes = gsl::span<uint8_t>;
inline uint16_t be16(bytes b, size_t p) { return uint16_t((b[p] << 8) | b[p+1]); }
inline uint32_t be32(bytes b, size_t p) { return (uint32_t(be16(b,p)) << 16) | be16(b,p+2); }
inline void put16(bytes b, size_t p, uint16_t v) { b[p]=uint8_t(v>>8); b[p+1]=uint8_t(v); }
inline mac read_mac(bytes b, size_t p) { mac m{}; std::copy_n(b.begin()+p,6,m.begin()); return m; }
inline void write_mac(bytes b, size_t p, const mac& m) { std::copy(m.begin(),m.end(),b.begin()+p); }
struct layout { uint16_t protocol{}; size_t ip_length{}, header{}, udp_length{}; bool dhcp{}; };
inline bool inspect(bytes b, layout& l) {
    l={};
    if(b.size()<14 || b.size()>1514) return false;
    l.protocol=be16(b,12);
    if(l.protocol==0x0806) {
        return b.size()>=42 && be16(b,14)==1 && be16(b,16)==0x0800 && b[18]==6 && b[19]==4 &&
            (be16(b,20)==1 || be16(b,20)==2);
    }
    if(l.protocol!=0x0800 || b.size()<34 || (b[14]>>4)!=4) return false;
    l.header=size_t(b[14]&15)*4; l.ip_length=be16(b,16);
    if(l.header<20 || l.ip_length<l.header || 14+l.ip_length>b.size()) return false;
    const auto fragment=be16(b,20);
    if((fragment&0x8000) || ((fragment&0x4000) && (fragment&0x3fff)) ||
        ((fragment&0x2000) && (l.ip_length-l.header)%8!=0) ||
        size_t(fragment&0x1fff)*8+l.ip_length>65535) return false;
    // IPv4 fragments need no transport parsing or checksum changes.
    if((be16(b,20)&0x3fff)!=0 || b[23]!=17) return true;
    if(l.ip_length<l.header+8) return false;
    const size_t u=14+l.header; l.udp_length=be16(b,u+4);
    if(l.udp_length<8 || l.udp_length>l.ip_length-l.header) return false;
    const auto src=be16(b,u), dst=be16(b,u+2);
    if((src==68 && dst==67) || (src==67 && dst==68)) {
        // BOOTP fixed header + DHCP cookie. Options are not interpreted.
        if(l.udp_length<248 || (b[u+8]!=1 && b[u+8]!=2) || b[u+9]!=1 || b[u+10]!=6 ||
            be32(b,u+8+236)!=0x63825363) return false;
        l.dhcp=true;
    }
    return true;
}
inline void broadcast_dhcp(bytes b, const layout& l) {
    if(!l.dhcp) return;
    const size_t u=14+l.header, d=u+8;
    if(b[d]!=1 || be16(b,d+10)!=0) return;
    put16(b,d+10,0x8000);
    // RFC 1624 incremental update of the BOOTP flags word; preserve disabled
    // UDP checksums. No IPv4 header field changes here.
    auto old=be16(b,u+6);
    if(old) {
        uint32_t sum=uint16_t(~old)+0xffffu+0x8000u;
        while(sum>>16) sum=(sum&0xffff)+(sum>>16);
        auto updated=uint16_t(~sum); put16(b,u+6,updated?updated:0xffff);
    }
}
}
#include "ipv6.hpp"
namespace reims {
class translator {
    mac wifi_,guest_;
    std::map<uint32_t,mac> addresses_;
    translator6 ipv6_;
public:
    translator(mac wifi, mac guest):wifi_(wifi),guest_(guest),ipv6_(wifi,guest) {}
    bool translate(bytes b, bool from_tap) {
        if(b.size()>=14 && be16(b,12)==0x86dd)return ipv6_.translate(b,from_tap);
        layout l;
        if(!inspect(b,l)) return false;
        if(from_tap) {
            if(read_mac(b,6)!=guest_) return false;
            if(l.protocol==0x0806 && read_mac(b,22)!=guest_) return false;
            const auto ip=be32(b,l.protocol==0x0806?28:26);
            const auto first=ip>>24;
            if(first>0 && first<224 && first!=127 && (ip>>16)!=0xa9fe && addresses_.size()<256) addresses_[ip]=guest_;
            if(l.protocol==0x0806) {
                write_mac(b,22,wifi_);
            } else broadcast_dhcp(b,l);
            write_mac(b,6,wifi_);
            return true;
        }
        if(read_mac(b,6)==wifi_) return false; // own reinjection/host send
        const auto ip=be32(b,l.protocol==0x0806?38:30);
        const auto target=addresses_.find(ip);
        if(target!=addresses_.end()) {
            write_mac(b,0,target->second);
            if(l.protocol==0x0806 && be16(b,20)==2) write_mac(b,32,target->second);
        } else if(!(b[0]&1)) return false; // host/other unicast stays on host
        return true;
    }
};
}
