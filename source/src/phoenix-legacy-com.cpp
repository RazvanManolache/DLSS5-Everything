// Process-local legacy COM routes. Never modifies machine COM registration,
// system DLLs or the game's EXE, and never substitutes successful API results.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <objbase.h>
#include <ddraw.h>
#include <cstdio>
#include <cstring>
#include <string>

namespace legacy_com {
constexpr GUID directdraw={0xd7b70ee0,0x4340,0x11cf,{0xb0,0x63,0,0x20,0xaf,0xc2,0xcd,0x35}};
constexpr GUID directdraw7={0x3c305196,0x50db,0x11d3,{0x9c,0xfe,0,0xc0,0x4f,0xd9,0x30,0xc5}};
constexpr GUID directplay_lobby={0x2fe8f810,0xb2a5,0x11d0,{0xa7,0x87,0,0,0xf8,3,0xab,0xfc}};
constexpr wchar_t applications[]=L"Software\\Microsoft\\DirectPlay\\Applications";
bool bridge_draw=false,bridge_registration=false;
std::wstring directory,log_path;
using Create=HRESULT(WINAPI*)(REFCLSID,LPUNKNOWN,DWORD,REFIID,LPVOID*);
Create original_create=CoCreateInstance;
decltype(&RegCreateKeyExW) original_reg_create_w=RegCreateKeyExW;
decltype(&RegCreateKeyExA) original_reg_create_a=RegCreateKeyExA;
decltype(&RegOpenKeyExW) original_reg_open_w=RegOpenKeyExW;
decltype(&RegOpenKeyExA) original_reg_open_a=RegOpenKeyExA;

void log(const char* operation,LONG result){
 if(log_path.empty())return;
 FILE* file=nullptr;if(_wfopen_s(&file,log_path.c_str(),L"a")==0 && file){std::fprintf(file,"%s result=0x%08lx\n",operation,result);std::fclose(file);}
}
bool registry_root(HKEY root,LPCWSTR path){return root==HKEY_LOCAL_MACHINE && path && !_wcsicmp(path,applications);}
bool registry_root(HKEY root,LPCSTR path){return root==HKEY_LOCAL_MACHINE && path && !_stricmp(path,"Software\\Microsoft\\DirectPlay\\Applications");}
LSTATUS WINAPI create_w(HKEY h,LPCWSTR s,DWORD r,LPWSTR cl,DWORD o,REGSAM a,LPSECURITY_ATTRIBUTES sa,PHKEY out,LPDWORD d){
 const bool redirected=registry_root(h,s);auto result=original_reg_create_w(redirected?HKEY_CURRENT_USER:h,s,r,cl,o,a,sa,out,d);if(redirected)log("DirectPlay user registration",result);return result;
}
LSTATUS WINAPI create_a(HKEY h,LPCSTR s,DWORD r,LPSTR cl,DWORD o,REGSAM a,LPSECURITY_ATTRIBUTES sa,PHKEY out,LPDWORD d){
 const bool redirected=registry_root(h,s);auto result=original_reg_create_a(redirected?HKEY_CURRENT_USER:h,s,r,cl,o,a,sa,out,d);if(redirected)log("DirectPlay user registration",result);return result;
}
LSTATUS WINAPI open_w(HKEY h,LPCWSTR s,DWORD o,REGSAM a,PHKEY out){return original_reg_open_w(registry_root(h,s)?HKEY_CURRENT_USER:h,s,o,a,out);}
LSTATUS WINAPI open_a(HKEY h,LPCSTR s,DWORD o,REGSAM a,PHKEY out){return original_reg_open_a(registry_root(h,s)?HKEY_CURRENT_USER:h,s,o,a,out);}

// Preserve existing per-user entries and mirror only missing machine entries,
// so enumeration still sees registered games after writes become per-user.
LSTATUS prepare_user_registry(){
 HKEY machine=nullptr,user=nullptr;auto status=RegOpenKeyExW(HKEY_LOCAL_MACHINE,applications,0,KEY_READ,&machine);
 if(status!=ERROR_SUCCESS)return status;
 status=RegCreateKeyExW(HKEY_CURRENT_USER,applications,0,nullptr,0,KEY_READ|KEY_WRITE,nullptr,&user,nullptr);
 if(status==ERROR_SUCCESS){
  for(DWORD index=0;;++index){
   wchar_t name[256];DWORD length=256;status=RegEnumKeyExW(machine,index,name,&length,nullptr,nullptr,nullptr,nullptr);
   if(status==ERROR_NO_MORE_ITEMS){status=ERROR_SUCCESS;break;}if(status!=ERROR_SUCCESS)break;
   HKEY source=nullptr,target=nullptr;DWORD disposition=0;
   status=RegOpenKeyExW(machine,name,0,KEY_READ,&source);if(status!=ERROR_SUCCESS)break;
   status=RegCreateKeyExW(user,name,0,nullptr,0,KEY_READ|KEY_WRITE,nullptr,&target,&disposition);
   if(status==ERROR_SUCCESS && disposition==REG_CREATED_NEW_KEY)status=RegCopyTreeW(source,nullptr,target);
   if(target)RegCloseKey(target);RegCloseKey(source);if(status!=ERROR_SUCCESS)break;
  }
  RegCloseKey(user);
 }
 RegCloseKey(machine);return status;
}
bool patch_import(HMODULE module,const char* name,void* replacement,void** original){
 auto base=reinterpret_cast<BYTE*>(module);auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
 if(!base || dos->e_magic!=IMAGE_DOS_SIGNATURE)return false;
 auto nt=reinterpret_cast<IMAGE_NT_HEADERS*>(base+dos->e_lfanew);if(nt->Signature!=IMAGE_NT_SIGNATURE)return false;
 const auto rva=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;if(!rva)return false;
 auto descriptor=reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base+rva);
 for(;descriptor->Name;++descriptor){
  if(!descriptor->OriginalFirstThunk)continue;
  auto names=reinterpret_cast<IMAGE_THUNK_DATA*>(base+descriptor->OriginalFirstThunk);
  auto slots=reinterpret_cast<IMAGE_THUNK_DATA*>(base+descriptor->FirstThunk);
  for(;names->u1.AddressOfData;++names,++slots){
   if(IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal))continue;
   auto item=reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base+names->u1.AddressOfData);
   if(std::strcmp(reinterpret_cast<char*>(item->Name),name))continue;
   if(reinterpret_cast<void*>(slots->u1.Function)==replacement)return true;
   DWORD old=0;if(!VirtualProtect(&slots->u1.Function,sizeof(void*),PAGE_READWRITE,&old))return false;
   *original=reinterpret_cast<void*>(slots->u1.Function);
   InterlockedExchangePointer(reinterpret_cast<void* volatile*>(&slots->u1.Function),replacement);
   DWORD ignored;VirtualProtect(&slots->u1.Function,sizeof(void*),old,&ignored);return true;
  }
 }
 return false;
}
void prepare_directplay(){
 auto module=GetModuleHandleW(L"dplayx.dll");if(!module)return;
 auto status=prepare_user_registry();log("Prepare per-user DirectPlay mirror",status);if(status!=ERROR_SUCCESS)return;
 patch_import(module,"RegCreateKeyExW",reinterpret_cast<void*>(&create_w),reinterpret_cast<void**>(&original_reg_create_w));
 patch_import(module,"RegCreateKeyExA",reinterpret_cast<void*>(&create_a),reinterpret_cast<void**>(&original_reg_create_a));
 patch_import(module,"RegOpenKeyExW",reinterpret_cast<void*>(&open_w),reinterpret_cast<void**>(&original_reg_open_w));
 patch_import(module,"RegOpenKeyExA",reinterpret_cast<void*>(&open_a),reinterpret_cast<void**>(&original_reg_open_a));
}
using ResolveFactory=HRESULT(*)(REFCLSID,IClassFactory**);
HRESULT local_factory(REFCLSID clsid,IClassFactory** out){
 const auto path=directory+L"\\DDraw.dll";auto module=GetModuleHandleW(path.c_str());if(!module)module=LoadLibraryW(path.c_str());
 if(!module)return HRESULT_FROM_WIN32(GetLastError());
 using GetClass=HRESULT(WINAPI*)(REFCLSID,REFIID,LPVOID*);
 auto entry=reinterpret_cast<GetClass>(GetProcAddress(module,"DllGetClassObject"));
 if(!entry)return CLASS_E_CLASSNOTAVAILABLE;
 return entry(clsid,IID_IClassFactory,reinterpret_cast<void**>(out));
}
ResolveFactory factory=local_factory;
bool draw_class(REFCLSID clsid){return IsEqualGUID(clsid,directdraw)||IsEqualGUID(clsid,directdraw7);}
HRESULT WINAPI create(REFCLSID clsid,LPUNKNOWN outer,DWORD context,REFIID iid,LPVOID* out){
 if(bridge_draw && draw_class(clsid) && (context&CLSCTX_INPROC_SERVER)){
  if(!out)return E_POINTER;*out=nullptr;IClassFactory* owner=nullptr;auto result=factory(clsid,&owner);
  if(SUCCEEDED(result)){
   if(!owner)return E_UNEXPECTED;
   result=owner->CreateInstance(outer,iid,out);owner->Release();
  }
  log("Local DirectDraw COM activation",result);return result;
 }
 auto result=original_create(clsid,outer,context,iid,out);
 if(bridge_registration && SUCCEEDED(result) && IsEqualGUID(clsid,directplay_lobby))prepare_directplay();
 return result;
}
}

#ifndef PHOENIX_LEGACY_CONTRACT_TEST
static void initialize(HMODULE module){
 wchar_t path[32768]{};if(!GetModuleFileNameW(module,path,32768))return;
 auto cut=wcsrchr(path,L'\\');if(!cut || _wcsicmp(cut+1,L"DDraw.dll"))return;
 *cut=0;legacy_com::directory=path;
 const auto ini=legacy_com::directory+L"\\PhoenixLegacy.ini";
 legacy_com::bridge_draw=GetPrivateProfileIntW(L"Compatibility",L"DirectDrawCOM",0,ini.c_str())==1;
 legacy_com::bridge_registration=GetPrivateProfileIntW(L"Compatibility",L"DirectPlayUserRegistration",0,ini.c_str())==1;
 legacy_com::log_path=legacy_com::directory+L"\\PhoenixLegacy.log";
 if(!legacy_com::bridge_draw && !legacy_com::bridge_registration)return;
 const bool installed=legacy_com::patch_import(GetModuleHandleW(nullptr),"CoCreateInstance",reinterpret_cast<void*>(&legacy_com::create),reinterpret_cast<void**>(&legacy_com::original_create));
 legacy_com::log("Install process-local legacy COM bridge",installed?ERROR_SUCCESS:ERROR_PROC_NOT_FOUND);
}
// This proxy is a static import of the game. It installs only an IAT pointer
// here; COM, registry mirroring and graphics calls run later from game code.
// Its lifetime therefore survives temporary graphics-provider probes.
BOOL WINAPI DllMain(HMODULE module,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){DisableThreadLibraryCalls(module);initialize(module);}return TRUE;}
#endif
