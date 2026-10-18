// ndisapi SDK and Ethernet MAC-NAT sample: Copyright NT KERNEL, MIT.
// Reims lifecycle, bounds checks and packet translation are authored here.
#include <winsock2.h>
#include <ws2ipdef.h>
#include <windows.h>
#include <tchar.h>
#include <wintrust.h>
#include <softpub.h>
#include <mscat.h>
#include <bcrypt.h>
#include <ntddndis.h>
#include <iphlpapi.h>
#include <netioapi.h>
#include <objbase.h>
#include <common.h>
#include <ndisapi.h>
#include "packet.hpp"
#include <filesystem>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <stdexcept>
#include <string>
#include <memory>
#include <vector>
#include <optional>
#include <cwctype>
#include <cstring>

namespace fs=std::filesystem;
struct handle {
    HANDLE value{};
    explicit handle(HANDLE h=nullptr):value(h) {}
    ~handle(){if(value && value!=INVALID_HANDLE_VALUE) CloseHandle(value);}
    handle(const handle&)=delete; handle& operator=(const handle&)=delete;
};
static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static std::wstring guid(std::wstring v){
    require(v.size()==38 && v.front()==L'{' && v.back()==L'}',"Expected braced GUID");
    for(size_t i=1;i<37;++i){
        if(i==9 || i==14 || i==19 || i==24) require(v[i]==L'-',"Invalid GUID separator");
        else require(iswxdigit(v[i])!=0,"Invalid GUID digit");
        v[i]=wchar_t(towlower(v[i]));
    }
    require(v!=L"{00000000-0000-0000-0000-000000000000}","Empty GUID");return v;
}
static std::string ascii(const std::wstring& v){std::string out;for(auto c:v){require(c<=127,"ASCII identity required");out+=static_cast<char>(c);}return out;}
static void require_router_discovery_disabled(const NET_LUID& tap){
    MIB_IPINTERFACE_ROW row{};row.Family=AF_INET6;row.InterfaceLuid=tap;
    require(GetIpInterfaceEntry(&row)==NO_ERROR && row.RouterDiscoveryBehavior==RouterDiscoveryDisabled,
        "Dedicated TAP RouterDiscovery must remain disabled before forwarding IPv6");
}
static unsigned long long number(const std::wstring& v){
    require(!v.empty() && v.find_first_not_of(L"0123456789")==v.npos,"Invalid numeric identity");
    size_t end{};auto n=std::stoull(v,&end);require(n && end==v.size(),"Invalid numeric identity");return n;
}
static unsigned long long ticks(HANDLE process){
    FILETIME created{},exit{},kernel{},user{};
    require(GetProcessTimes(process,&created,&exit,&kernel,&user)!=0,"Cannot identify QEMU creation time");
    return ((uint64_t(created.dwHighDateTime)<<32)|created.dwLowDateTime)+504911232000000000ULL;
}
static reims::mac parse_mac(const std::wstring& text){
    require(text.size()==17,"Invalid guest MAC");reims::mac m{};
    for(size_t i=0;i<6;++i){
        const size_t p=i*3;require(iswxdigit(text[p]) && iswxdigit(text[p+1]) && (i==5 || text[p+2]==L':'),"Invalid guest MAC");
        m[i]=uint8_t(std::stoul(text.substr(p,2),nullptr,16));
    }
    require(!(m[0]&1) && m!=reims::mac{},"Unicast guest MAC required");return m;
}
static std::string escaped(const std::string& v){
    std::string out;for(unsigned char c:v){if(c=='"'||c=='\\')out+='\\';out+=c<32?' ':char(c);}return out;
}
static void status(const fs::path& folder,const std::string& session,const char* state,
    unsigned long pid,unsigned long long start,const std::string& error="",bool cleanup=false,const std::string& filters="null"){
    const auto temp=folder/L"status.json.new",target=folder/L"status.json";
    {std::ofstream f(temp,std::ios::binary|std::ios::trunc);require(bool(f),"Cannot write forwarder state");
    f<<"{\"schema\":1,\"session\":\""<<session<<"\",\"state\":\""<<state
     <<"\",\"qemu_pid\":"<<pid<<",\"qemu_start_ticks\":"<<start
     <<",\"helper_pid\":"<<GetCurrentProcessId()<<",\"cleanup_confirmed\":"<<(cleanup?"true":"false")
     <<",\"ipv6_enabled\":"<<(std::string(state)=="ready"?"true":"false")
     <<",\"hardware_filters\":"<<filters<<",\"error\":\""<<escaped(error)<<"\"}";
    f.flush();require(bool(f),"Cannot flush forwarder state");}
    require(MoveFileExW(temp.c_str(),target.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=0,"Cannot commit forwarder state");
}
static bool verify_catalog(const std::wstring& catalog,const std::wstring& file){
    handle member(CreateFileW(file.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,0,nullptr));
    if(member.value==INVALID_HANDLE_VALUE)return false;
    HCATADMIN admin{};
    if(!CryptCATAdminAcquireContext2(&admin,nullptr,BCRYPT_SHA256_ALGORITHM,nullptr,0))return false;
    DWORD size{};bool valid=false;
    if(CryptCATAdminCalcHashFromFileHandle2(admin,member.value,&size,nullptr,0) && size && size<=128){
        std::vector<BYTE> hash(size);
        if(CryptCATAdminCalcHashFromFileHandle2(admin,member.value,&size,hash.data(),0)){
            std::wostringstream tag;tag<<std::uppercase<<std::hex<<std::setfill(L'0');
            for(auto b:hash)tag<<std::setw(2)<<unsigned(b);
            const auto text=tag.str();
            WINTRUST_CATALOG_INFO info{};info.cbStruct=sizeof(info);info.pcwszCatalogFilePath=catalog.c_str();
            info.pcwszMemberFilePath=file.c_str();info.pcwszMemberTag=text.c_str();info.hMemberFile=member.value;
            info.pbCalculatedFileHash=hash.data();info.cbCalculatedFileHash=size;info.hCatAdmin=admin;
            WINTRUST_DATA data{};data.cbStruct=sizeof(data);data.dwUIChoice=WTD_UI_NONE;
            data.fdwRevocationChecks=WTD_REVOKE_WHOLECHAIN;data.dwUnionChoice=WTD_CHOICE_CATALOG;data.pCatalog=&info;
            data.dwStateAction=WTD_STATEACTION_VERIFY;
            GUID action=WINTRUST_ACTION_GENERIC_VERIFY_V2;
            valid=WinVerifyTrust(nullptr,&action,&data)==ERROR_SUCCESS;
            data.dwStateAction=WTD_STATEACTION_CLOSE;WinVerifyTrust(nullptr,&action,&data);
        }
    }
    CryptCATAdminReleaseContext(admin,0);return valid;
}
struct capture {
    CNdisApi& api;HANDLE adapter{};handle event{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    handle mutex;bool attached{},owned{},filter_owned{},filter_before_read{},filter_read{},filter_restored{};DWORD previous_filter{},our_filter{},readback_filter{};bool& cleanup_confirmed;std::string* receipt;
    capture(CNdisApi& a,HANDLE h,const std::wstring& id,bool& cleaned,std::string* saved=nullptr):api(a),adapter(h),
        mutex(CreateMutexW(nullptr,TRUE,(L"Global\\ReimsWifiMacNat-"+id).c_str())),cleanup_confirmed(cleaned),receipt(saved){
        const auto existing=GetLastError()==ERROR_ALREADY_EXISTS;
        require(mutex.value && !existing,"Selected adapter already reserved by another helper");
        require(event.value!=nullptr,"Cannot create capture event");
    }
    void start(DWORD additional_filter=0){
        ADAPTER_MODE mode{adapter,0};
        require(api.GetAdapterMode(&mode)!=0 && mode.dwFlags==0,"Unknown or active foreign NDIS mode; refusing capture");
        if(additional_filter){
            require(api.GetHwPacketFilter(adapter,&previous_filter)!=0,"Cannot record selected hardware filter");
            filter_before_read=true;
            our_filter=previous_filter|additional_filter;
            if(our_filter!=previous_filter){
                if(!api.SetHwPacketFilter(adapter,our_filter)){cleanup_confirmed=false;throw std::runtime_error("Cannot confirm owned hardware filter");}filter_owned=true;
            }
            DWORD confirmed{};
            require(api.GetHwPacketFilter(adapter,&confirmed)!=0,"Cannot read back selected hardware filter");
            readback_filter=confirmed;filter_read=true;
            if(confirmed!=our_filter){
                if(confirmed==previous_filter)filter_owned=false;
                throw std::runtime_error("Required selected hardware filter was not confirmed; relay not ready");
            }
        }
        require(api.SetPacketEvent(adapter,event.value)!=0,"Cannot attach capture event");attached=true;
        mode.dwFlags=MSTCP_FLAG_SENT_LISTEN|MSTCP_FLAG_RECV_LISTEN|MSTCP_FLAG_FILTER_DIRECT|MSTCP_FLAG_LOOPBACK_BLOCK;
        if(!api.SetAdapterMode(&mode)){cleanup_confirmed=false;throw std::runtime_error("Cannot confirm selected capture mode");}owned=true;
    }
    bool stop(){
        bool ok=true;
        if(owned){
            ADAPTER_MODE current{adapter,0};
            const DWORD expected=MSTCP_FLAG_SENT_LISTEN|MSTCP_FLAG_RECV_LISTEN|MSTCP_FLAG_FILTER_DIRECT|MSTCP_FLAG_LOOPBACK_BLOCK;
            if(!api.GetAdapterMode(&current) || current.dwFlags!=expected){owned=false;attached=false;filter_owned=false;cleanup_confirmed=false;return false;}
            ADAPTER_MODE mode{adapter,0};ok=api.SetAdapterMode(&mode)!=0;owned=false;ok=(api.FlushAdapterPacketQueue(adapter)!=0)&&ok;
        }
        if(attached){ok=(api.SetPacketEvent(adapter,nullptr)!=0)&&ok;attached=false;}
        if(filter_owned){
            DWORD current{};
            if(!api.GetHwPacketFilter(adapter,&current) || current!=our_filter)ok=false;
            else{
                DWORD restored{};
                filter_restored=api.SetHwPacketFilter(adapter,previous_filter)!=0 &&
                    api.GetHwPacketFilter(adapter,&restored)!=0 && restored==previous_filter;
                ok=filter_restored&&ok;
            }
            filter_owned=false;
        }
        cleanup_confirmed=ok && cleanup_confirmed;return ok;
    }
    ~capture(){cleanup_confirmed=stop() && cleanup_confirmed;if(receipt)*receipt=filter_receipt();}
    std::string filter_receipt()const{
        return "{\"before\":"+(filter_before_read?std::to_string(previous_filter):"null")+",\"requested\":"+(filter_before_read?std::to_string(our_filter):"null")+
            ",\"read_back\":"+(filter_read?std::to_string(readback_filter):"null")+",\"restore_confirmed\":"+(filter_restored?"true":"false")+"}";
    }
};
static size_t find_adapter(const TCP_AdapterList& list,const std::wstring& id){
    std::optional<size_t> found;
    for(size_t i=0;i<list.m_nAdapterCount;++i){
        const auto raw=reinterpret_cast<const char*>(list.m_szAdapterNameList[i]);
        const auto length=strnlen(raw,ADAPTER_NAME_SIZE);
        require(length<ADAPTER_NAME_SIZE,"Unterminated adapter identity");
        std::string s(raw,length);const auto p=s.find('{');if(p==s.npos)continue;
        const auto suffix=s.substr(p);std::wstring wide(suffix.begin(),suffix.end());
        if(guid(wide)!=id)continue;
        require(!found,"Ambiguous selected adapter GUID");found=i;
    }
    require(bool(found),"Selected adapter is missing from packet-filter inventory");return *found;
}
int wmain(int argc,wchar_t** argv){
    fs::path folder;std::string session,filters="null",tap_filters="null",wifi_filters="null";unsigned long pid{};unsigned long long start{};bool cleanup_confirmed=true;
    try{
        if(argc==4 && std::wstring(argv[1])==L"--verify-catalog")return verify_catalog(argv[2],argv[3])?0:1;
        if(argc==4 && std::wstring(argv[1])==L"--check"){
            const auto tap=guid(argv[2]),wifi=guid(argv[3]);require(tap!=wifi,"Two distinct adapters required");
            CNdisApi api;require(api.IsDriverLoaded()!=0,"Driver unavailable");TCP_AdapterList list{};
            require(api.GetTcpipBoundAdaptersInfo(&list)!=0 && list.m_nAdapterCount<=ADAPTER_LIST_SIZE,"Adapter inventory unavailable");
            capture a(api,list.m_nAdapterHandle[find_adapter(list,tap)],tap,cleanup_confirmed),b(api,list.m_nAdapterHandle[find_adapter(list,wifi)],wifi,cleanup_confirmed);
            for(auto adapter:{a.adapter,b.adapter}){ADAPTER_MODE m{adapter,0};require(api.GetAdapterMode(&m)!=0 && !m.dwFlags,"Unknown or active foreign NDIS mode");}
            return 0;
        }
        if(argc==2 && std::wstring(argv[1])==L"--self-test"){
            require(guid(L"{ABCDEFAB-1234-4234-8234-123456789ABC}")==L"{abcdefab-1234-4234-8234-123456789abc}","GUID check failed");
            parse_mac(L"52:54:00:11:22:33");return 0;
        }
        require(argc==8,"Usage: reims-wifi-macnat TAP-GUID WiFi-GUID guest-MAC QEMU-PID QEMU-start-ticks state-directory session-GUID");
        const auto tap=guid(argv[1]),wifi=guid(argv[2]);require(tap!=wifi,"Two distinct adapters required");
        const auto guest=parse_mac(argv[3]);const auto rawpid=number(argv[4]);require(rawpid<=MAXDWORD,"Invalid QEMU PID");pid=DWORD(rawpid);start=number(argv[5]);
        folder=fs::path(argv[6]);require(folder.is_absolute() && fs::is_directory(folder),"Existing absolute VM state directory required");
        session=ascii(guid(argv[7]));
        handle qemu(OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid));
        require(qemu.value && ticks(qemu.value)==start && WaitForSingleObject(qemu.value,0)==WAIT_TIMEOUT,"QEMU identity changed or exited");
        status(folder,session,"starting",pid,start);
        GUID tap_guid{};NET_LUID tap_luid{};
        require(CLSIDFromString(tap.c_str(),&tap_guid)==S_OK && ConvertInterfaceGuidToLuid(&tap_guid,&tap_luid)==NO_ERROR,"Cannot resolve dedicated TAP IPv6 identity");
        require_router_discovery_disabled(tap_luid);
        CNdisApi api;require(api.IsDriverLoaded()!=0,"Packet filter driver unavailable");TCP_AdapterList list{};
        require(api.GetTcpipBoundAdaptersInfo(&list)!=0 && list.m_nAdapterCount<=ADAPTER_LIST_SIZE,"Cannot read adapter inventory");
        const auto ti=find_adapter(list,tap),wi=find_adapter(list,wifi);
        require((list.m_nAdapterMediumList[ti]==0 || list.m_nAdapterMediumList[ti]==16) &&
                (list.m_nAdapterMediumList[wi]==0 || list.m_nAdapterMediumList[wi]==16),"Unsupported adapter medium");
        capture tap_capture(api,list.m_nAdapterHandle[ti],tap,cleanup_confirmed,&tap_filters),wifi_capture(api,list.m_nAdapterHandle[wi],wifi,cleanup_confirmed,&wifi_filters);
        tap_capture.start(NDIS_PACKET_TYPE_PROMISCUOUS);
        // Receive solicited-node multicast for guest addresses, not just groups
        // joined by the host's own IPv6 stack. Restore this filter on cleanup.
        wifi_capture.start(NDIS_PACKET_TYPE_ALL_MULTICAST);
        filters="{\"tap\":"+tap_capture.filter_receipt()+",\"wifi\":"+wifi_capture.filter_receipt()+"}";
        reims::mac wifi_mac{};std::copy_n(list.m_czCurrentAddress[wi],6,wifi_mac.begin());
        reims::translator translator(wifi_mac,guest);
        status(folder,session,"ready",pid,start,"",false,filters);
        const HANDLE events[]{qemu.value,tap_capture.event.value,wifi_capture.event.value};
        auto packet=std::make_unique<INTERMEDIATE_BUFFER>();
        while(WaitForSingleObject(qemu.value,0)==WAIT_TIMEOUT && !fs::exists(folder/L"stop")){
            require_router_discovery_disabled(tap_luid);
            const auto waited=WaitForMultipleObjects(3,events,FALSE,100);
            require(waited!=WAIT_FAILED,"Capture wait failed");
            if(waited==WAIT_OBJECT_0)break;
            for(unsigned side=0;side<2;++side){
                auto& input=side?wifi_capture:tap_capture;auto& output=side?tap_capture:wifi_capture;
                ResetEvent(input.event.value);
                ETH_REQUEST request{};request.hAdapterHandle=input.adapter;request.EthPacket.Buffer=packet.get();
                // Bounded batches ensure stop/QEMU-exit checks cannot starve under traffic.
                for(unsigned count=0;count<256 && api.ReadPacket(&request);++count){
                    if(packet->m_dwDeviceFlags!=PACKET_FLAG_ON_RECEIVE || packet->m_Length>sizeof(packet->m_IBuffer))continue;
                    if(packet->m_Length>=14 && reims::be16(reims::bytes(packet->m_IBuffer,packet->m_Length),12)==0x86dd){
                        reims::layout6 layout;
                        if(reims::inspect6(reims::bytes(packet->m_IBuffer,packet->m_Length),layout) && layout.nd==134)
                            require_router_discovery_disabled(tap_luid);
                    }
                    if(!translator.translate(reims::bytes(packet->m_IBuffer,packet->m_Length),side==0))continue;
                    request.hAdapterHandle=output.adapter;
                    require(api.SendPacketToAdapter(&request)!=0,"Packet reinjection failed");
                    request.hAdapterHandle=input.adapter;
                }
                DWORD remaining{};
                require(api.GetAdapterPacketQueueSize(input.adapter,&remaining)!=0,"Selected capture queue disappeared");
                if(remaining)SetEvent(input.event.value);
            }
        }
        const bool cleaned_wifi=wifi_capture.stop(),cleaned_tap=tap_capture.stop();
        require(cleaned_wifi && cleaned_tap,"Owned capture cleanup failed; inspect driver state before retrying");
        filters="{\"tap\":"+tap_capture.filter_receipt()+",\"wifi\":"+wifi_capture.filter_receipt()+"}";
        status(folder,session,"stopped",pid,start,"",true,filters);return 0;
    }catch(const std::exception& error){
        filters="{\"tap\":"+tap_filters+",\"wifi\":"+wifi_filters+"}";
        if(!folder.empty() && !session.empty())try{status(folder,session,"error",pid,start,error.what(),cleanup_confirmed,filters);}catch(...){}
        return 1;
    }
}
