#pragma once
// Windows owns the interface's multicast filtering. This socket adds/removes
// only its own group references; no adapter multicast list is replaced.
struct multicast_memberships {
    SOCKET socket_{INVALID_SOCKET};DWORD interface_{};bool winsock_{},released_{};
    std::map<reims::ip6,uint64_t> groups_;uint64_t clock_{},joins_{},evictions_{};
    bool& cleanup_;std::string& saved_;
    const reims::ip6 all_nodes{0xff,2,0,0,0,0,0,0,0,0,0,0,0,0,0,1};
    const reims::ip6 mdns{0xff,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0xfb};
    multicast_memberships(bool& cleanup,std::string& saved):cleanup_(cleanup),saved_(saved){}
    void option(const reims::ip6& group,int operation){
        ipv6_mreq request{};std::copy(group.begin(),group.end(),request.ipv6mr_multiaddr.u.Byte);request.ipv6mr_interface=interface_;
        if(setsockopt(socket_,IPPROTO_IPV6,operation,reinterpret_cast<const char*>(&request),sizeof(request))==SOCKET_ERROR)
            throw std::runtime_error("Owned IPv6 multicast membership operation failed: "+std::to_string(WSAGetLastError()));
    }
    bool join(const reims::ip6& group){
        require(socket_!=INVALID_SOCKET && group[0]==0xff,"Invalid owned multicast membership");
        const auto found=groups_.find(group);
        if(found!=groups_.end()){found->second=++clock_;return false;}
        if(groups_.size()>=256){
            auto oldest=groups_.end();
            for(auto it=groups_.begin();it!=groups_.end();++it)
                if(it->first!=all_nodes && it->first!=mdns && (oldest==groups_.end() || it->second<oldest->second))oldest=it;
            require(oldest!=groups_.end(),"Owned multicast membership limit reached");
            option(oldest->first,IPV6_LEAVE_GROUP);groups_.erase(oldest);++evictions_;
        }
        option(group,IPV6_JOIN_GROUP);groups_[group]=++clock_;++joins_;return true;
    }
    void start(DWORD index){
        require(index!=0,"Explicit Wi-Fi IPv6 interface index required");interface_=index;
        WSADATA data{};require(WSAStartup(MAKEWORD(2,2),&data)==0,"Cannot initialize owned multicast socket");winsock_=true;
        socket_=WSASocketW(AF_INET6,SOCK_DGRAM,IPPROTO_UDP,nullptr,0,WSA_FLAG_NO_HANDLE_INHERIT);
        require(socket_!=INVALID_SOCKET,"Cannot create owned IPv6 multicast socket");
        join(all_nodes);join(mdns);
    }
    std::string receipt()const{
        return "{\"interface_index\":"+std::to_string(interface_)+",\"socket_open\":"+(socket_!=INVALID_SOCKET?"true":"false")+
            ",\"joined_groups\":"+std::to_string(groups_.size())+",\"joins\":"+std::to_string(joins_)+
            ",\"evictions\":"+std::to_string(evictions_)+",\"released\":"+(released_?"true":"false")+"}";
    }
    bool close(decltype(&::closesocket) close_socket=::closesocket,decltype(&::WSACleanup) cleanup_winsock=::WSACleanup){
        bool ok=true;
        if(socket_!=INVALID_SOCKET){
            const auto owned=socket_;socket_=INVALID_SOCKET;
            ok=close_socket(owned)==0;if(ok)released_=true;
        }
        // Final WSACleanup may deallocate a socket even after closesocket fails.
        // Both handles become terminal; preserve failure instead of retrying it.
        if(winsock_){winsock_=false;ok=(cleanup_winsock()==0)&&ok;}
        cleanup_=ok&&cleanup_;saved_=receipt();return ok;
    }
    ~multicast_memberships(){close();}
};
