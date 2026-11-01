#include <windows.h>
#include <iostream>
#include <cstring>
#include <stdexcept>
#include "Bridge.h"
#include "PanelState.h"
#include "CapsTransitions.h"
#include "OsdFramePolicy.h"
namespace {
int passed=0, calls=0, scenario=0;
void require(bool condition, char const* message) { if(!condition)throw std::runtime_error(message); ++passed; }
template<class F> void rejects(F run,char const* message) { bool caught=false;try{run();}catch(std::exception const&){caught=true;} require(caught,message); }
std::int32_t __cdecl fake(std::uint32_t,std::uint32_t,unsigned char* buffer,std::size_t capacity) {
    ++calls;
    if(scenario==1)return -2;
    if(scenario==2)return 1024*1024+1;
    if(scenario==3) { if(buffer)std::memset(buffer,'x',capacity);return 4; }
    if(scenario==4)return static_cast<std::int32_t>(capacity+4);
    if(scenario==5) { if(buffer){buffer[0]=0xFF;buffer[1]=0;}return 2; }
    if(scenario==6) { if(buffer){buffer[0]='{';buffer[1]=0;buffer[2]='}';buffer[3]=0;}return 4; }
    if(scenario==7) { if(!buffer)return 3; if(capacity<4)return 4;std::memcpy(buffer,"{ }",4);return 4; }
    if(buffer && capacity>=3)std::memcpy(buffer,"{}",3);
    return 3;
}
}
int wmain(int argc,wchar_t** argv) {
    using namespace openrevo;
    try {
        PanelState state; require(state.minimize(false)==false && !state.hidden,"tray failure retains mini");
        state.expand();require(!state.mini && !state.hidden,"expand full");
        require(state.minimize(false) && state.mini && !state.hidden,"full minimize creates visible mini without tray");
        require(state.hide(true) && state.hidden,"X hides to tray");state.wake();require(!state.hidden && state.mini,"wake remembers mini");
        require(state.minimize(true) && state.hidden,"mini minimize hides");state.exiting=true;state.wake();state.expand();require(state.hidden && state.mini && !state.hide(true),"late wake during exit ignored");
        CapsTransitions caps{false};require(!caps.accept(0x13,true),"ignore other key");require(caps.accept(0x14,true)==true,"caps locks");require(!caps.accept(0x14,true),"autorepeat suppressed");require(!caps.accept(0x14,false),"keyup does not toggle");require(caps.accept(0x14,true)==false,"caps unlocks");
        require(readQuery(fake,ReadQuery::Provenance)=="{}","caller buffer response");
        for(scenario=1;scenario<=6;++scenario)rejects([&]{readQuery(fake,ReadQuery::Provenance);},"invalid ABI rejected");
        scenario=7;require(readQuery(fake,ReadQuery::Provenance)=="{ }","buffer growth retried");
        calls=0;rejects([&]{readQuery(fake,static_cast<ReadQuery>(20));},"allowlist enforced");require(calls==0,"disallowed query never invoked");
        rejects([&]{readQuery(fake,ReadQuery::FactoryFanTable,4);},"invalid mode rejected");require(calls==0,"invalid mode never invoked");
        rejects([&]{ReadOnlyBridge bridge(L"openrevo_native_bridge.dll");},"relative DLL rejected");
        require((OsdFramePolicy::style(0x94CF0000)&OsdFramePolicy::frameBits)==0,"OSD frame cleared");
        require((OsdFramePolicy::extended(0x080001A8)&OsdFramePolicy::edgeBits)==0,"OSD edges cleared");
        require((OsdFramePolicy::extended(0)&0x08000020)==0x08000020,"OSD no activate and click through");
        if(argc!=2)throw std::runtime_error("Expected absolute baseline bridge path");
        for(int i=0;i<4;++i) { ReadOnlyBridge bridge(argv[1]);auto payload=bridge.provenance();require(payload.find("72e98685b2c0538fa0a58409a6bcce9e7b6336f7")!=std::string::npos,"real bridge pinned revision");require(payload.find("\"hardware_writes\":false")!=std::string::npos,"real bridge query only"); }
        std::cout<<"{\"passed\":true,\"checks\":"<<passed<<",\"hardwareWrites\":false}\n";return 0;
    }catch(std::exception const& e){std::cerr<<e.what()<<'\n';return 1;}
}
