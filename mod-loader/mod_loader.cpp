#include "mod_loader.h"
#include <windows.h>
#include <stdio.h>
#include <dwmapi.h>

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

	WIN32_FIND_DATAA findData;
	HANDLE hFind = FindFirstFileA(".\\mods\\*.dll", &findData);

	if (hFind == INVALID_HANDLE_VALUE) {
		LogModLoaderMessage("No mods found or mods directory doesn't exist.");
		return;
	}

	int modCount = 0;
	do {
		char modPath[MAX_PATH];
		sprintf_s(modPath, ".\\mods\\%s", findData.cFileName);

		HMODULE hMod = LoadLibraryA(modPath);
		if (hMod) {
			char msg[256];
			sprintf_s(msg, "Loaded mod: %s", findData.cFileName);
			LogModLoaderMessage(msg);

			// Try to call the mod's initialization function
			typedef void (*ModInitFunc)();
			ModInitFunc modInit = (ModInitFunc)GetProcAddress(hMod, "ModInit");
			if (modInit) {
				sprintf_s(msg, "Initializing %s...", findData.cFileName);
				LogModLoaderMessage(msg);
				modInit();
			}
			else {
				sprintf_s(msg, "Warning: %s has no ModInit() function",
					findData.cFileName);
				LogModLoaderMessage(msg);
			}

			modCount++;
		}
		else {
			char msg[256];
			sprintf_s(msg, "Failed to load: %s (Error: %d)", findData.cFileName,
				GetLastError());
			LogModLoaderMessage(msg);
		}
	} while (FindNextFileA(hFind, &findData));

	FindClose(hFind);

	char msg[256];
	sprintf_s(msg, "Loaded %d mod(s)", modCount);
	LogModLoaderMessage(msg);
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