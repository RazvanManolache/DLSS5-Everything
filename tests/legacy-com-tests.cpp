#define PHOENIX_LEGACY_CONTRACT_TEST
#include "../source/src/phoenix-legacy-com.cpp"
#include <cstdlib>

namespace {
unsigned checks=0,forwarded=0,resolved=0,created=0,released=0;
HKEY last_root=nullptr;REGSAM last_access=0;HRESULT result=E_ACCESSDENIED;
void check(bool value){++checks;if(!value)std::abort();}
LSTATUS WINAPI reg_create_w(HKEY root,LPCWSTR,DWORD,LPWSTR,DWORD,REGSAM access,LPSECURITY_ATTRIBUTES,PHKEY,LPDWORD){last_root=root;last_access=access;return ERROR_ACCESS_DENIED;}
LSTATUS WINAPI reg_create_a(HKEY root,LPCSTR,DWORD,LPSTR,DWORD,REGSAM access,LPSECURITY_ATTRIBUTES,PHKEY,LPDWORD){last_root=root;last_access=access;return ERROR_ACCESS_DENIED;}
LSTATUS WINAPI reg_open_w(HKEY root,LPCWSTR,DWORD,REGSAM access,PHKEY){last_root=root;last_access=access;return ERROR_FILE_NOT_FOUND;}
LSTATUS WINAPI reg_open_a(HKEY root,LPCSTR,DWORD,REGSAM access,PHKEY){last_root=root;last_access=access;return ERROR_FILE_NOT_FOUND;}
HRESULT WINAPI forward(REFCLSID,LPUNKNOWN,DWORD,REFIID,LPVOID*){++forwarded;return E_NOINTERFACE;}
class Factory:public IClassFactory {
 HRESULT WINAPI QueryInterface(REFIID,void**)override{return E_NOINTERFACE;}
 ULONG WINAPI AddRef()override{return 1;}
 ULONG WINAPI Release()override{++released;return 1;}
 HRESULT WINAPI CreateInstance(IUnknown*,REFIID,void** out)override{++created;if(SUCCEEDED(result))*out=reinterpret_cast<void*>(0x1234);return result;}
 HRESULT WINAPI LockServer(BOOL)override{return S_OK;}
} owner;
HRESULT resolve(REFCLSID,IClassFactory** out){++resolved;*out=&owner;return S_OK;}
HRESULT fail_factory(REFCLSID,IClassFactory**){return CLASS_E_CLASSNOTAVAILABLE;}
}
int main(){
 using namespace legacy_com;
 original_reg_create_w=reg_create_w;original_reg_create_a=reg_create_a;
 original_reg_open_w=reg_open_w;original_reg_open_a=reg_open_a;
 HKEY out=nullptr;
 check(create_w(HKEY_LOCAL_MACHINE,applications,0,nullptr,0,KEY_ALL_ACCESS,nullptr,&out,nullptr)==ERROR_ACCESS_DENIED);
 check(last_root==HKEY_CURRENT_USER && last_access==KEY_ALL_ACCESS);
 check(create_a(HKEY_LOCAL_MACHINE,"SOFTWARE\\Microsoft\\DirectPlay\\Applications",0,nullptr,0,KEY_WRITE,nullptr,&out,nullptr)==ERROR_ACCESS_DENIED);
 check(last_root==HKEY_CURRENT_USER && last_access==KEY_WRITE);
 check(open_w(HKEY_LOCAL_MACHINE,applications,0,KEY_READ,&out)==ERROR_FILE_NOT_FOUND && last_root==HKEY_CURRENT_USER);
 check(open_a(HKEY_LOCAL_MACHINE,"Software\\Microsoft\\DirectPlay\\Applications",0,KEY_READ,&out)==ERROR_FILE_NOT_FOUND && last_root==HKEY_CURRENT_USER);
 for(auto root:{HKEY_CURRENT_USER,reinterpret_cast<HKEY>(0x1234)}){
  create_w(root,applications,0,nullptr,0,KEY_READ,nullptr,&out,nullptr);check(last_root==root);
 }
 open_w(HKEY_LOCAL_MACHINE,L"Software\\Other",0,KEY_READ,&out);check(last_root==HKEY_LOCAL_MACHINE);
 create_a(HKEY_LOCAL_MACHINE,nullptr,0,nullptr,0,KEY_READ,nullptr,&out,nullptr);check(last_root==HKEY_LOCAL_MACHINE);
 original_create=forward;factory=resolve;bridge_draw=true;bridge_registration=false;GUID unknown{};void* object=nullptr;
 check(create(unknown,nullptr,CLSCTX_ALL,IID_IUnknown,&object)==E_NOINTERFACE && forwarded==1 && resolved==0);
 check(create(directdraw,nullptr,CLSCTX_LOCAL_SERVER,IID_IUnknown,&object)==E_NOINTERFACE && forwarded==2 && resolved==0);
 for(const auto& clsid:{directdraw,directdraw7}){
  result=E_ACCESSDENIED;check(create(clsid,nullptr,CLSCTX_ALL,IID_IUnknown,&object)==E_ACCESSDENIED && object==nullptr);
  result=S_OK;check(create(clsid,nullptr,CLSCTX_ALL,IID_IUnknown,&object)==S_OK && object==reinterpret_cast<void*>(0x1234));
 }
 check(created==4 && released==4 && forwarded==2);
 check(create(directdraw,nullptr,CLSCTX_ALL,IID_IUnknown,nullptr)==E_POINTER);
 factory=fail_factory;object=reinterpret_cast<void*>(0x5678);
 check(create(directdraw,nullptr,CLSCTX_ALL,IID_IUnknown,&object)==CLASS_E_CLASSNOTAVAILABLE && object==nullptr && forwarded==2);
 bridge_draw=false;check(create(directdraw,nullptr,CLSCTX_ALL,IID_IUnknown,&object)==E_NOINTERFACE && forwarded==3);
 std::printf("{\"passed\":true,\"cpu_only\":true,\"checks\":%u,\"bits\":%u,\"api_errors_preserved\":true}\n",checks,unsigned(sizeof(void*)*8));
}
