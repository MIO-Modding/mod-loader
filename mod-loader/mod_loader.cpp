#include "mod_loader.h"
#include <windows.h>
#include <stdio.h>
#include <dwmapi.h>
#include <string>
#include <nlohmann/json.hpp>
#include <filesystem>
#include <iostream>
#include <fstream>

namespace fs = std::filesystem;

bool IsTargetExecutable() {
	char path[MAX_PATH];
	if (!GetModuleFileNameA(nullptr, path, MAX_PATH)) {
		return false;
	}

	// Extract filename from full path
	const char* exeName = strrchr(path, '\\');
	exeName = exeName ? exeName + 1 : path;

	// Check if this is MIO.exe (case-insensitive)
	return _stricmp(exeName, "MIO.exe") == 0;
}
//Stops double running of the loader when the exe is ran directly instead of from steam
bool IsCorrectRun() {
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
void LoadMods() {
	LogModLoaderMessage("Loading mods from ./mods/ directory...");

	class ModLoadInfo {
	public:
		fs::path dllPath;
		std::string id;
		std::string name;
		std::vector<std::string> dependencies;
		ModLoadInfo() {}
		ModLoadInfo(fs::path dllPath, std::string id, std::string name, std::vector<std::string> dependencies) {
			this->dllPath = dllPath;
			this->id = id;
			this->name = name;
			this->dependencies = dependencies;
		}
		void Load(std::map<std::string, ModLoadInfo>* modLoadInfos, std::vector<std::string>* loadedMods, std::vector<std::string> loadStack) {
			loadStack.push_back(id);
			for (std::string i : dependencies) {
				if (!std::count(loadedMods->begin(), loadedMods->end(), i)) {
					modLoadInfos->at(i).Load(modLoadInfos, loadedMods, loadStack);
				}
			}
			std::erase(loadStack, id);
			loadedMods->push_back(id);
			HMODULE hMod = LoadLibraryA(dllPath.string().c_str());
			if (hMod) {
				LogModLoaderMessage(std::string("Loaded mod: " + id).c_str());

				// Try to call the mod's initialization function
				typedef void (*ModInitFunc)();
				ModInitFunc modInit = (ModInitFunc)GetProcAddress(hMod, "ModInit");
				if (modInit) {
					LogModLoaderMessage(std::string("Initializing " + id + "...").c_str());
					modInit();
				}
				else {
					LogModLoaderMessage(std::string("Warning: " + id + " has no ModInit() function").c_str());
				}
			}
			else {
				LogModLoaderMessage(std::string("Failed to load: " + id + " (Error: " + std::to_string(GetLastError()) + ")").c_str());
			}
		}
	};

	std::map<std::string, ModLoadInfo> dllsToLoad;
	for (fs::directory_entry i : fs::directory_iterator(".\\mods")) {
		if (fs::is_directory(i.status())) {
			fs::path dirPath = i.path();
			fs::path modJsonPath = dirPath / fs::path("mod.json");
			std::ifstream file(modJsonPath);
			nlohmann::json data = nlohmann::json::parse(file);
			file.close();
			std::string id = data["id"].get<std::string>();
			std::string name = data["name"].get<std::string>();
			fs::path mainDll = dirPath / fs::path(data["main"].get<std::string>());
			std::vector<std::string> dependencies = data["dependencies"].get<std::vector<std::string>>();
			dllsToLoad[id] = ModLoadInfo(mainDll, id, name, dependencies);
		}
	}
	std::vector<std::string> loadedMods;
	std::vector<ModLoadInfo> rootDlls;
	for (auto& i : dllsToLoad) {
		bool anyDependants = false;
		for (auto& j : dllsToLoad) {
			if (std::count(j.second.dependencies.begin(), j.second.dependencies.end(), i.second.id)) {
				anyDependants = true;
				break;
			}
		}
		if (!anyDependants) {
			i.second.Load(&dllsToLoad, &loadedMods, std::vector<std::string>());
		}
	}

	LogModLoaderMessage(std::string("Loaded " + std::to_string(loadedMods.size()) + " mod(s)").c_str());
}

void InitializeModLoader() {
	// Create mods directory if it doesn't exist
	CreateDirectoryA(".\\mods", NULL);

	// Create modconfig directory if it doesn't exist
	CreateDirectoryA(".\\modconfig", NULL);

	AllocConsole();
	FILE* f;
	freopen_s(&f, "CONOUT$", "w", stdout);

	printf("==============================================\n");
	printf("        MIO Mod Loader v%d.%d.%d\n", MOD_LOADER_VERSION_MAJOR, MOD_LOADER_VERSION_MINOR, MOD_LOADER_VERSION_PATCH);
	printf("==============================================\n");

	LogModLoaderMessage("Mod Loader initialized!");

	// Disable DWM for GUI mods (needed on some systems)
	DisableDWM();

	LoadMods();
}