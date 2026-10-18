#include "packet.hpp"
#include <iostream>
#include <stdexcept>
#include <vector>
using namespace reims;
static void check(bool value) { if(!value) throw std::runtime_error("Packet fixture failed"); }
int main() {
    mac guest{2,1,2,3,4,5}, wifi{2,6,7,8,9,10};
    translator t(wifi,guest);
    std::vector<uint8_t> arp(42); bytes a(arp.data(),arp.size());
    write_mac(a,6,guest); put16(a,12,0x0806); put16(a,14,1); put16(a,16,0x0800);
    a[18]=6;a[19]=4;put16(a,20,1);write_mac(a,22,guest);a[28]=192;a[29]=168;a[30]=1;a[31]=50;
    auto original=arp;
    for(size_t i=0;i<42;++i) check(!t.translate(bytes(arp.data(),i),true));
    check(t.translate(a,true));check(read_mac(a,6)==wifi && read_mac(a,22)==wifi);
    arp=original; a=bytes(arp.data(),arp.size());a[18]=5;check(!t.translate(a,true));
    arp=original;a=bytes(arp.data(),arp.size());put16(a,12,0x86dd);check(!t.translate(a,true));
    std::vector<uint8_t> ip(14+20+248); bytes b(ip.data(),ip.size());
    write_mac(b,6,guest);put16(b,12,0x0800);b[14]=0x45;put16(b,16,268);b[23]=17;
    put16(b,34,68);put16(b,36,67);put16(b,38,248);
    b[42]=1;b[43]=1;b[44]=6;b[278]=0x63;b[279]=0x82;b[280]=0x53;b[281]=0x63;
    auto dhcp=ip;
    for(size_t i=14;i<ip.size();++i) check(!t.translate(bytes(ip.data(),i),true));
    check(t.translate(b,true));check(be16(b,52)==0x8000 && be16(b,40)==0);
    ip=dhcp;b=bytes(ip.data(),ip.size());put16(b,40,0x1234);
    check(t.translate(b,true));check(be16(b,40)==0x9233);
    ip=dhcp;b=bytes(ip.data(),ip.size());b[14]=0x44;check(!t.translate(b,true));
    ip=dhcp;b=bytes(ip.data(),ip.size());put16(b,38,7);check(!t.translate(b,true));
    ip=dhcp;b=bytes(ip.data(),ip.size());put16(b,38,249);check(!t.translate(b,true));
    ip=dhcp;b=bytes(ip.data(),ip.size());b[281]=0;check(!t.translate(b,true));
    ip=dhcp;b=bytes(ip.data(),ip.size());put16(b,20,0x2000);check(t.translate(b,true));
    ip=dhcp;b=bytes(ip.data(),ip.size());put16(b,20,0x8000);check(!t.translate(b,true));
    ip=dhcp;b=bytes(ip.data(),ip.size());put16(b,20,0x6000);check(!t.translate(b,true));
    ip=dhcp;b=bytes(ip.data(),ip.size());b[23]=6;put16(b,20,0);b[30]=192;b[31]=168;b[32]=1;b[33]=50;
    write_mac(b,6,mac{2,9,9,9,9,9});write_mac(b,0,wifi);
    check(t.translate(b,false));check(read_mac(b,0)==guest);
    b[33]=51;check(!t.translate(b,false));b[0]=0xff;check(t.translate(b,false));
    write_mac(b,6,wifi);check(!t.translate(b,false));
    // Restart with a warm guest ARP cache: no ARP packet reaches this fresh relay.
    translator restarted(wifi,guest);
    std::vector<uint8_t> warm(34);bytes w(warm.data(),warm.size());
    write_mac(w,6,guest);put16(w,12,0x0800);w[14]=0x45;put16(w,16,20);w[23]=6;
    w[26]=192;w[27]=0;w[28]=2;w[29]=50;
    check(restarted.translate(w,true));
    write_mac(w,6,mac{2,9,9,9,9,9});write_mac(w,0,wifi);w[30]=192;w[31]=0;w[32]=2;w[33]=50;
    check(restarted.translate(w,false));check(read_mac(w,0)==guest);
    for(const uint32_t source:{0x00010203u,0x7f000001u,0xa9fe0102u,0xe0000001u,0xffffffffu}){
        translator invalid(wifi,guest);write_mac(w,6,guest);
        for(unsigned i=0;i<4;++i)w[26+i]=uint8_t(source>>(24-i*8));
        check(invalid.translate(w,true));
        write_mac(w,6,mac{2,9,9,9,9,9});write_mac(w,0,wifi);
        for(unsigned i=0;i<4;++i)w[30+i]=uint8_t(source>>(24-i*8));
        check(!invalid.translate(w,false));
    }
    // Exhaustively visit Ethernet/IPv4 prefix lengths with valid backing storage.
    for(unsigned ihl=0;ihl<16;++ihl) for(unsigned total=0;total<70;++total) {
        std::vector<uint8_t> v(90);bytes p(v.data(),v.size());put16(p,12,0x0800);p[14]=uint8_t(0x40|ihl);put16(p,16,uint16_t(total));layout l;
        check(inspect(p,l)==(ihl>=5 && total>=ihl*4));
    }
    std::cout<<"PASS bounded Ethernet/ARP/IPv4/UDP/DHCP, MAC translation, IPv6 refusal fixtures\n";
}
