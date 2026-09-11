#include "mod_loader.h"
#include <windows.h>
#include <stdio.h>
#include <dwmapi.h>
#include <string>
#include <nlohmann/json.hpp>
#include <filesystem>
#include <iostream>
#include <fstream>
#define NETHOST_USE_AS_STATIC 1
#include <nethost.h>
#include <coreclr_delegates.h>
#include <hostfxr.h>

namespace fs = std::filesystem;

bool IsTargetExecutable() {
	char path[MAX_PATH];
	if (!GetModuleFileNameA(nullptr, path, MAX_PATH)) {
		return false;
	}

	std::filesystem::path fullPath(path);
	return fullPath.filename().string() == std::string("mio.exe");
}
//Stops double running of the loader when the exe is ran directly instead of from steam
bool IsCorrectRun() {
	//Disabling this method, Apparently it doesnt work on windows 11!
	return true;

	std::wstring wstr = std::wstring(GetCommandLineW());
	std::wstring sub = wstr.substr(1, wstr.substr(1).find('"'));
	std::wstring mioExeName = L"mio.exe";
	if (wstr.find('"') == 0 && sub.substr(sub.length() - mioExeName.length(), mioExeName.length()) == mioExeName) {
		return false;
	}
	return true;
}

void LogModLoaderMessage(const char* message) {
	printf("[LOADER] %s\n", message);
}

Version GetModLoaderVersion() {
	Version v = { MOD_LOADER_VERSION_MAJOR, MOD_LOADER_VERSION_MINOR, MOD_LOADER_VERSION_PATCH };

	return v;
}

void DisableDWM() {
	HKEY hKey;
	LSTATUS lResult;
	const wchar_t* subKeyPath = L"Software\\Microsoft\\Windows "
		L"NT\\CurrentVersion\\AppCompatFlags\\Layers";
	const wchar_t* valueName =
		L"C:\\Program Files (x86)\\Steam\\steamapps\\common\\MIO\\mio.exe";
	const wchar_t* valueData = L"~ DISABLEDXMAXIMIZEDWINDOWEDMODE";

	lResult = RegCreateKeyExW(HKEY_CURRENT_USER, subKeyPath, 0, NULL,
		REG_OPTION_NON_VOLATILE, KEY_ALL_ACCESS, NULL,
		&hKey, NULL);

	if (lResult != ERROR_SUCCESS) {
		printf("Error creating/opening registry key! GUI mods might not work.\n");
		return;
	}

	DWORD dataSize = (DWORD)((wcslen(valueData) + 1) * sizeof(wchar_t));

	lResult =
		RegSetValueExW(hKey, valueName, 0, REG_SZ, (LPBYTE)valueData, dataSize);

	if (lResult != ERROR_SUCCESS) {
		printf("Error setting registry key! GUI mods might not work.\n");
		return;
	}
	else {
		printf("DWM rendering disabled successfully. Please relaunch your game if "
			"GUI mods continue to not work.\n");
	}

	RegCloseKey(hKey);
}
std::vector<std::wstring> GetLaunchArguments() {
	std::vector<std::wstring> arguments;
	int argc = 0;

	LPWSTR cmdLine = GetCommandLineW();
	if (!cmdLine) return arguments;

	LPWSTR* argv = CommandLineToArgvW(cmdLine, &argc);
	if (!argv) return arguments;

	arguments.reserve(argc);
	for (int i = 0; i < argc; ++i) {
		arguments.push_back(argv[i]);
	}
	LocalFree(argv);
	return arguments;
}
std::wstring GetArgument(std::vector<std::wstring> args, std::wstring arg, std::wstring defaultResult) {
	auto it = std::find(args.begin(), args.end(), arg);
	if (it != args.end() && (it + 1) != args.end()) {
		return *(it + 1);
	}
	return defaultResult;
}

std::string WideToNarrow(const std::wstring& wstr) {
	if (wstr.empty()) return "";

	int sizeNeeded = WideCharToMultiByte(CP_UTF8, 0, &wstr[0], (int)wstr.size(), NULL, 0, NULL, NULL);
	std::string strTo(sizeNeeded, 0);
	WideCharToMultiByte(CP_UTF8, 0, &wstr[0], (int)wstr.size(), &strTo[0], sizeNeeded, NULL, NULL);
	return strTo;
}

void __stdcall LogMessage(const char* message) {
	printf("%s\n", message); 
	fflush(stdout);
}

void InitializeModLoader() {
	std::vector<std::wstring> launchArgs = GetLaunchArguments();
	std::string modsPath = WideToNarrow(GetArgument(launchArgs, L"--mods-path", L"mods"));
	std::string modsConfigPath = WideToNarrow(GetArgument(launchArgs, L"--mods-config-path", L"modconfig"));


	// Create mods directory if it doesn't exist
	CreateDirectoryA(modsPath.c_str(), NULL);

	// Create modconfig directory if it doesn't exist
	CreateDirectoryA(modsConfigPath.c_str(), NULL);

	if (AllocConsole()) {
		FILE* f;
		freopen_s(&f, "CONOUT$", "w", stdout);
	}

	printf("==============================================\n");
	printf("        MIO Mod Loader v%d.%d.%d\n", MOD_LOADER_VERSION_MAJOR, MOD_LOADER_VERSION_MINOR, MOD_LOADER_VERSION_PATCH);
	printf("==============================================\n");

	// Disable DWM for GUI mods (needed on some systems)
	// Disabled this cause idk what this does
	//DisableDWM();

	HMODULE hModule = GetModuleHandleA("mio.exe");
	if (!hModule) {
		hModule = GetModuleHandleA(nullptr);
		if (!hModule) {
			LogModLoaderMessage("ERROR: Failed to get mio.exe module handle!");
			Sleep(2000);
			return;
		}
	}


	fs::path configPath = (fs::current_path() / "mio-mod-loader" / "MioModLoader.runtimeconfig.json").make_preferred();
	fs::path assemblyPath = (fs::current_path() / "mio-mod-loader" / "MioModLoader.dll").make_preferred();

	char_t buffer[MAX_PATH];
	size_t bufferSize = sizeof(buffer) / sizeof(char_t);
	int get_hostfxr_rc = get_hostfxr_path(buffer, &bufferSize, nullptr);
	fs::path hostfxrPath;
	if (get_hostfxr_rc == 0) {
		hostfxrPath = buffer;
	} else {
		fs::path localHostfxr = (fs::current_path() / "mio-mod-loader" / "hostfxr.dll").make_preferred();
		if (fs::exists(localHostfxr)) {
			hostfxrPath = localHostfxr;
		} else {
			LogModLoaderMessage("Could not locate hostfxr.dll via nethost or local directory!");
			Sleep(2000);
			return;
		}
	}
	fs::path preferredHostfxr = hostfxrPath.make_preferred();
	HMODULE lib = LoadLibraryW(preferredHostfxr.c_str());
	if (!lib) {
		LogModLoaderMessage("Failed to load hostfxr.dll module");
		Sleep(2000);
		return;
	}
	auto initFptr = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(lib, "hostfxr_initialize_for_runtime_config");
	auto getDelegateFptr = (hostfxr_get_runtime_delegate_fn)GetProcAddress(lib, "hostfxr_get_runtime_delegate");
	auto closeFptr = (hostfxr_close_fn)GetProcAddress(lib, "hostfxr_close");

	if (!initFptr || !getDelegateFptr || !closeFptr) {
		LogModLoaderMessage("Failed to get hostfxr function pointers from loaded module");
		FreeLibrary(lib);
		Sleep(2000);
		return;
	}
	std::wstring configPathStr = configPath.make_preferred().wstring();

	hostfxr_handle ctx = nullptr;
	int rc = initFptr(configPathStr.c_str(), nullptr, &ctx);

	if (rc != 0 || ctx == nullptr) {
		LogModLoaderMessage("Failed to initialize hostfxr");
		if (ctx && closeFptr) closeFptr(ctx);
		FreeLibrary(lib);
		Sleep(2000);
		return;
	}

	void* loadAssembly = nullptr;
	rc = getDelegateFptr(ctx, hdt_load_assembly_and_get_function_pointer, &loadAssembly);
	if (rc != 0 || loadAssembly == nullptr) {
		LogModLoaderMessage("Failed to retrieve runtime delegate");
		closeFptr(ctx);
		Sleep(2000);
		return;
	}

	auto loadAssemblyAndGetFunctionPointer = (load_assembly_and_get_function_pointer_fn)loadAssembly;
	void(*modInit)(void*, void*, void*, void*) = nullptr;

	rc = loadAssemblyAndGetFunctionPointer(
		assemblyPath.c_str(),
		L"MioModLoader.ModLoader, MioModLoader",
		L"LoadModsPointers",
		UNMANAGEDCALLERSONLY_METHOD,
		nullptr,
		(void**)&modInit
	);

	if (rc != 0 || modInit == nullptr) {
		LogModLoaderMessage("Failed to resolve LoadMods entrypoint");
		closeFptr(ctx);
		Sleep(2000);
		return;
	}

	modInit((void*)modsPath.c_str(), (void*)modsConfigPath.c_str(), &LogMessage, hModule);
	closeFptr(ctx);
}