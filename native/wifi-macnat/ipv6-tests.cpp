#include "packet.hpp"
#include <vector>
#include <iostream>
#include <stdexcept>
#include <string>
using namespace reims;
static int assertions=0;
static void check(bool value){++assertions;if(!value)throw std::runtime_error("IPv6 fixture assertion "+std::to_string(assertions));}
static const mac guest{2,1,2,3,4,5},wifi{2,6,7,8,9,10},peer{2,11,12,13,14,15};
static const ip6 local{0xfe,0x80,0,0,0,0,0,0,0,0,0,0,0,0,0x12,0x34};
static const ip6 global{0x20,1,0x0d,0xb8,0,0,0,0,0,0,0,0,0,0,0x12,0x34};
static const ip6 router{0xfe,0x80,0,0,0,0,0,0,0,0,0,0,0,0,0,1};
static const ip6 all_nodes{0xff,2,0,0,0,0,0,0,0,0,0,0,0,0,0,1};
static const ip6 all_routers{0xff,2,0,0,0,0,0,0,0,0,0,0,0,0,0,2};
static ip6 solicited(ip6 target){ip6 ip{0xff,2,0,0,0,0,0,0,0,0,0,1,0xff,0,0,0};std::copy(target.begin()+13,target.end(),ip.begin()+13);return ip;}
static bytes span(std::vector<uint8_t>& b){return {b.data(),b.size()};}
static void put_ip(std::vector<uint8_t>& b,size_t at,const ip6& ip){std::copy(ip.begin(),ip.end(),b.begin()+at);}
static std::vector<uint8_t> frame(uint8_t next,std::vector<uint8_t> payload,ip6 src=local,ip6 dst=all_nodes){
    std::vector<uint8_t> b(54+payload.size());auto p=span(b);
    write_mac(p,0,dst[0]==0xff?mac{0x33,0x33,dst[12],dst[13],dst[14],dst[15]}:peer);
    write_mac(p,6,guest);put16(p,12,0x86dd);
    b[14]=0x60;put16(p,18,uint16_t(payload.size()));b[20]=next;b[21]=255;put_ip(b,22,src);put_ip(b,38,dst);
    std::copy(payload.begin(),payload.end(),b.begin()+54);return b;
}
// Independent reference checksum over a constructed pseudo-header, not the
// production parser/checksum. These fixtures are genuine checksummed packets.
static uint16_t reference(std::vector<uint8_t> b,size_t upper=54){
    const size_t end=54+(size_t(b[18])<<8)+b[19],length=end-upper;
    std::vector<uint8_t> pseudo(b.begin()+22,b.begin()+54);
    for(int shift=24;shift>=0;shift-=8)pseudo.push_back(uint8_t(length>>shift));
    pseudo.insert(pseudo.end(),{0,0,0,58});pseudo.insert(pseudo.end(),b.begin()+upper,b.begin()+end);
    if(pseudo.size()%2)pseudo.push_back(0);
    uint64_t sum=0;for(size_t i=0;i<pseudo.size();i+=2)sum+=uint16_t((pseudo[i]<<8)|pseudo[i+1]);
    while(sum>0xffff)sum=(sum&0xffff)+(sum>>16);return uint16_t(~sum);
}
static void sign(std::vector<uint8_t>& b,size_t upper=54){b[upper+2]=b[upper+3]=0;put16(span(b),upper+2,reference(b,upper));}
static std::vector<uint8_t> nd(uint8_t type,ip6 src,ip6 dst,ip6 target=router,bool option=true){
    const size_t fixed=type==133?8:type==134?16:type==137?40:24;
    std::vector<uint8_t> payload(fixed+(option?8:0));payload[0]=type;
    auto b=frame(58,payload,src,dst);
    if(type>=135)put_ip(b,62,target);
    if(type==137)put_ip(b,78,global);
    if(option){b[54+fixed]=type==136?2:1;b[55+fixed]=1;write_mac(span(b),56+fixed,guest);}
    sign(b);return b;
}
int main(){
    for(auto type:{133,135,136}){
        translator relay(wifi,guest);
        auto b=nd(uint8_t(type),local,type==133?all_routers:type==135?solicited(router):all_nodes,type==136?local:router);
        check(reference(b)==0);const auto before=b;
        for(size_t i=0;i<b.size();++i)check(!relay.translate(bytes(b.data(),i),true));
        check(relay.translate(span(b),true));check(read_mac(span(b),6)==wifi);check(reference(b)==0);
        const size_t option=type==133?62:78;check(read_mac(span(b),option+2)==wifi);
        check(!std::equal(before.begin()+56,before.begin()+58,b.begin()+56));
    }
    auto ns=nd(135,local,solicited(router));
    const auto ns_interests=guest_multicast_interests(span(ns),guest);
    check(ns_interests.size()==1 && ns_interests[0]==solicited(local)); // Not the neighbor's target address.
    auto dad_interest=nd(135,ip6{},solicited(global),global,false);
    const auto tentative_interests=guest_multicast_interests(span(dad_interest),guest);
    check(tentative_interests.size()==1 && tentative_interests[0]==solicited(global));
    auto invalid_interest=ns;invalid_interest.back()^=1;
    check(guest_multicast_interests(span(invalid_interest),guest).empty());
    write_mac(span(invalid_interest),6,peer);check(guest_multicast_interests(span(invalid_interest),guest).empty());
    check(guest_multicast_interests(bytes(ns.data(),ns.size()-1),guest).empty());
    for(int defect=0;defect<6;++defect){
        translator relay(wifi,guest);auto b=ns;
        if(defect==0)b.back()^=1; // Bad original checksum must not be laundered.
        if(defect==1){b[21]=254;sign(b);}
        if(defect==2){b[55]=1;sign(b);}
        if(defect==3){b[79]=0;sign(b);}
        if(defect==4){b[79]=2;sign(b);}
        if(defect==5){put_ip(b,62,all_nodes);sign(b);}
        check(!relay.translate(span(b),true));
        auto incoming=frame(17,std::vector<uint8_t>(8),router,local);write_mac(span(incoming),6,peer);write_mac(span(incoming),0,wifi);
        check(!relay.translate(span(incoming),false)); // Invalid ND did not learn.
    }
    translator dad(wifi,guest);auto d=nd(135,ip6{},solicited(global),global,false);
    check(dad.translate(span(d),true));check(read_ip6(span(d),22)==ip6{} && reference(d)==0);
    auto incoming=frame(17,std::vector<uint8_t>(8),router,global);write_mac(span(incoming),6,peer);write_mac(span(incoming),0,wifi);
    check(!dad.translate(span(incoming),false)); // Tentative DAD target not owned.
    auto duplicate=nd(136,router,all_nodes,global);write_mac(span(duplicate),6,peer);write_mac(span(duplicate),80,peer);sign(duplicate);
    check(dad.translate(span(duplicate),false));check(read_mac(span(duplicate),80)==peer && reference(duplicate)==0);
    auto bad_dad=nd(135,ip6{},solicited(global),global,true);check(!dad.translate(span(bad_dad),true));
    bad_dad=nd(135,ip6{},all_nodes,global,false);check(!dad.translate(span(bad_dad),true));
    auto bad_na=nd(136,local,all_nodes,local);bad_na[58]=0x40;sign(bad_na);check(!dad.translate(span(bad_na),true));
    // Warm relay restart: IPv6 data source learns without any NS or NA.
    translator warm(wifi,guest);auto outgoing=frame(17,std::vector<uint8_t>(8),global,router);
    auto payload=std::vector<uint8_t>(outgoing.begin()+14,outgoing.end());
    check(warm.translate(span(outgoing),true));check(std::equal(payload.begin(),payload.end(),outgoing.begin()+14));
    check(warm.translate(span(incoming),false));check(read_mac(span(incoming),0)==guest);
    for(const auto& address:{ip6{},ip6{0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1},all_nodes}){
        translator fresh(wifi,guest);auto b=frame(17,std::vector<uint8_t>(8),address,router);check(!fresh.translate(span(b),true));
    }
    // Multicast RA with prefix-independent RDNSS and router SLLA stays intact.
    std::vector<uint8_t> ra(48);ra[0]=134;ra[6]=7;ra[7]=8;ra[16]=25;ra[17]=3;ra[23]=120;ra[40]=1;ra[41]=1;
    auto advertisement=frame(58,ra,router,all_nodes);put_ip(advertisement,78,global);write_mac(span(advertisement),6,peer);write_mac(span(advertisement),96,peer);sign(advertisement);
    const auto original_ra=std::vector<uint8_t>(advertisement.begin()+14,advertisement.end());
    check(warm.translate(span(advertisement),false));check(std::equal(original_ra.begin(),original_ra.end(),advertisement.begin()+14));
    check(read_mac(span(advertisement),0)==mac{0x33,0x33,0,0,0,1});
    // ND after Destination Options: upper-layer pseudo length excludes extension.
    auto rs=nd(133,local,all_routers);std::vector<uint8_t> extended{58,0,0,0,0,0,0,0};extended.insert(extended.end(),rs.begin()+54,rs.end());
    auto ext=frame(60,extended,local,all_routers);sign(ext,62);
    check(warm.translate(span(ext),true));check(reference(ext,62)==0 && read_mac(span(ext),72)==wifi);
    // MLD report with Hop-by-Hop Router Alert may have unspecified source.
    std::vector<uint8_t> mld{58,0,5,2,0,0,0,0};mld.resize(32);mld[8]=131;
    auto report=frame(0,mld,ip6{},solicited(global));put_ip(report,70,solicited(global));report[21]=1;sign(report,62);const auto original_report=report;
    check(warm.translate(span(report),true));check(std::equal(original_report.begin()+14,original_report.end(),report.begin()+14));
    // Bounded extension chain and malformed advertised extension length.
    auto broken=ext;broken[55]=10;check(!warm.translate(span(broken),true));
    std::vector<uint8_t> chain(8*20+8);for(size_t i=0;i<20;++i)chain[i*8]=i==19?17:60;
    auto many=frame(60,chain);check(warm.translate(span(many),true));
    // Ordinary fragmented data is L2-only; first/atomic fragmented ND is refused.
    std::vector<uint8_t> fragment(16);fragment[0]=6;fragment[3]=1;
    auto first=frame(44,fragment,global,router);const auto first_copy=first;
    check(warm.translate(span(first),true));check(std::equal(first_copy.begin()+14,first_copy.end(),first.begin()+14));
    fragment[3]=8;auto noninitial=frame(44,fragment,global,router);check(warm.translate(span(noninitial),true));
    fragment[2]=0xff;fragment[3]=0xf9;auto overflow=frame(44,fragment);check(!warm.translate(span(overflow),true));
    fragment[2]=0;fragment[3]=2;auto reserved=frame(44,fragment);check(!warm.translate(span(reserved),true));
    std::vector<uint8_t> fragmented_nd{58,0,0,0,0,0,0,0};fragmented_nd.insert(fragmented_nd.end(),ns.begin()+54,ns.end());
    auto atomic=frame(44,fragmented_nd,local,solicited(router));sign(atomic,62);check(!warm.translate(span(atomic),true));
    fragmented_nd[3]=1;auto nd_first=frame(44,fragmented_nd,local,solicited(router));sign(nd_first,62);check(!warm.translate(span(nd_first),true));
    auto ah=frame(51,std::vector<uint8_t>{58,0,0,0,0,0,0,0});check(!warm.translate(span(ah),true));
    translator rotated(wifi,guest);ip6 privacy=global;
    for(unsigned i=1;i<=300;++i){privacy[14]=uint8_t(i>>8);privacy[15]=uint8_t(i);auto b=frame(253,{1,2,3,4},privacy,router);check(rotated.translate(span(b),true));}
    auto latest=frame(253,{1,2,3,4},router,privacy);write_mac(span(latest),6,peer);write_mac(span(latest),0,wifi);
    check(rotated.translate(span(latest),false));privacy[14]=0;privacy[15]=1;
    auto evicted=frame(253,{1,2,3,4},router,privacy);write_mac(span(evicted),6,peer);check(!rotated.translate(span(evicted),false));
    for(size_t i=0;i<ext.size();++i)check(!warm.translate(bytes(ext.data(),i),true));
    std::cout<<"PASS "<<assertions<<" IPv6/NDP checksum, bounds, DAD, warm learning, MLD/RA/DNS and fragment assertions\n";
}
