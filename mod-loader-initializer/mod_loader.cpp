#include "mod_loader.h"
#include <windows.h>
#include <stdio.h>
#include <dwmapi.h>
#include <string>
#include <vector>
#include <algorithm>
#include <cwctype>
#include <nlohmann/json.hpp>
#include <filesystem>
#include <iostream>
#include <fstream>
#define NETHOST_USE_AS_STATIC 1
#include <nethost.h>
#include <coreclr_delegates.h>
#include <hostfxr.h>

namespace fs = std::filesystem;

// ---------------------------------------------------------------------------
// Console
//
// The log window shows the loader's and mods' output. The game logs to the same
// window through win32_io::write_console, which on its first call checks whether
// standard output is a console: if it is, it writes to CONOUT$ directly; if not,
// it writes to the standard output handle. So standard output is pointed at a
// pipe before the game starts, and a reader thread drops the game's lines, or
// shows them with --game-logs. The game still writes every line to its log file
// itself, so the files are unchanged.
//
// Everything shown in the window goes through ConsoleWrite, under one lock, so
// lines from the game, the loader and mods never interleave mid-line.
// ---------------------------------------------------------------------------

static HANDLE g_console = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION g_consoleLock;
static bool g_rich = true;       // colors (off with --plain-logs, or if the console can't show them)
static bool g_gameLogs = false;  // show the game's own output (--game-logs)

// Flags passed to the managed loader; keep in step with ModLoader.LoadModsPointers.
// Bits 2-3 hold the minimum log level (0 Debug, 1 Info, 2 Warning, 3 Error).
enum LogFlags : int {
	LogFlags_Rich = 1 << 0,
	LogFlags_GameLogs = 1 << 1,
	LogFlags_LevelShift = 2,
	LogFlags_LevelMask = 3 << 2,
};

static int g_minLogLevel = 1;  // Info (--log-level)

static void ConsoleWrite(const char* text, size_t length) {
	if (g_console == INVALID_HANDLE_VALUE) return;
	EnterCriticalSection(&g_consoleLock);
	DWORD written = 0;
	WriteFile(g_console, text, (DWORD)length, &written, nullptr);
	LeaveCriticalSection(&g_consoleLock);
}

static void ConsoleWriteLine(const std::string& line) {
	std::string withNewline = line + "\n";
	ConsoleWrite(withNewline.data(), withNewline.size());
}

static std::string Timestamp() {
	SYSTEMTIME t;
	GetLocalTime(&t);
	char buffer[32];
	snprintf(buffer, sizeof(buffer), "[%02d:%02d:%02d.%03d]", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
	return buffer;
}

// A loader line from native code, in the same layout the managed loader uses. Native lines are
// Info, so they're hidden when --log-level is warning or error.
static void NativeLog(const std::string& message) {
	if (g_minLogLevel > 1) return;
	if (g_rich) {
		ConsoleWriteLine("\x1b[90m" + Timestamp() + "\x1b[0m [\x1b[38;2;95;215;255mLOADER\x1b[0m] " + message);
	}
	else {
		ConsoleWriteLine(Timestamp() + " [LOADER] " + message);
	}
}

static DWORD WINAPI GameOutputReader(LPVOID pipe) {
	std::string pending;
	char buffer[4096];
	DWORD read = 0;
	while (ReadFile((HANDLE)pipe, buffer, sizeof(buffer), &read, nullptr) && read > 0) {
		if (!g_gameLogs) continue;  // keep draining, or the game would block on a full pipe

		pending.append(buffer, read);
		size_t newline;
		while ((newline = pending.find('\n')) != std::string::npos) {
			std::string line = pending.substr(0, newline);
			if (!line.empty() && line.back() == '\r') line.pop_back();
			pending.erase(0, newline + 1);
			ConsoleWriteLine(g_rich ? "\x1b[90m[GAME] " + line + "\x1b[0m" : "[GAME] " + line);
		}
	}
	return 0;
}

static void SetUpConsole(bool rich, bool gameLogs) {
	InitializeCriticalSection(&g_consoleLock);
	g_gameLogs = gameLogs;

	AllocConsole();
	SetConsoleOutputCP(CP_UTF8);
	g_console = CreateFileA("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
		nullptr, OPEN_EXISTING, 0, nullptr);

	DWORD mode = 0;
	g_rich = rich && GetConsoleMode(g_console, &mode) &&
		SetConsoleMode(g_console, mode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING);

	// The game's console output goes into a pipe from here on (see above). Standard
	// error stays on the console, so runtime fatal errors are still shown.
	HANDLE readEnd = nullptr, writeEnd = nullptr;
	if (CreatePipe(&readEnd, &writeEnd, nullptr, 1 << 16)) {
		SetStdHandle(STD_OUTPUT_HANDLE, writeEnd);
		CloseHandle(CreateThread(nullptr, 0, GameOutputReader, readEnd, 0, nullptr));
	}
	else {
		NativeLog("Couldn't separate the game's output; it will show in this window.");
	}
}

// ---------------------------------------------------------------------------
// .NET runtime
//
// Releases ship their own copy of the .NET runtime in mio-mod-loader/dotnet, so
// players don't have to install .NET, on Windows or in Proton's prefix. Without
// that folder (e.g. a local build), an installed runtime is used, as before.
// ---------------------------------------------------------------------------

static bool IsRunningUnderWine() {
	HMODULE ntdll = GetModuleHandleA("ntdll.dll");
	return ntdll && GetProcAddress(ntdll, "wine_get_version") != nullptr;
}

static void ShowDotNetError() {
	const char* message = IsRunningUnderWine()
		? "MIO Mod Loader couldn't start .NET, so mods won't load.\n\n"
		"Reinstall the mod loader, making sure the mio-mod-loader\\dotnet folder is included. "
		"Or install .NET 8 into Proton's prefix with:\n\n"
		"protontricks 1672810 dotnet8"
		: "MIO Mod Loader couldn't start .NET, so mods won't load.\n\n"
		"Reinstall the mod loader, making sure the mio-mod-loader\\dotnet folder is included. "
		"Or install the .NET 8 Runtime (x64) from:\n\n"
		"https://dotnet.microsoft.com/download/dotnet/8.0";
	MessageBoxA(nullptr, message, "MIO Mod Loader", MB_OK | MB_ICONERROR);
}

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
	NativeLog(message);
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
		NativeLog("Error creating/opening registry key! GUI mods might not work.");
		return;
	}

	DWORD dataSize = (DWORD)((wcslen(valueData) + 1) * sizeof(wchar_t));

	lResult =
		RegSetValueExW(hKey, valueName, 0, REG_SZ, (LPBYTE)valueData, dataSize);

	if (lResult != ERROR_SUCCESS) {
		NativeLog("Error setting registry key! GUI mods might not work.");
		return;
	}
	else {
		NativeLog("DWM rendering disabled successfully. Please relaunch your game if "
			"GUI mods continue to not work.");
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
	int ind = std::find(args.begin(), args.end(), arg) - args.begin();
	if (ind + 1 < args.capacity()) {
		return args[ind + 1];
	}
	return defaultResult;
}
bool HasArgument(std::vector<std::wstring> args, std::wstring arg) {
	auto result = std::find(args.begin(), args.end(), arg);
	if (result != args.end()) {
		return true;
	}
	return false;
}

// --log-level <debug|info|warning|error>: the least important level shown and written. Default info.
int ParseLogLevel(const std::vector<std::wstring>& args, bool& valid) {
	valid = true;
	auto it = std::find(args.begin(), args.end(), std::wstring(L"--log-level"));
	if (it == args.end()) return 1;
	if (it + 1 == args.end()) { valid = false; return 1; }

	std::wstring value = *(it + 1);
	for (wchar_t& c : value) c = (wchar_t)towlower(c);
	if (value == L"debug") return 0;
	if (value == L"info") return 1;
	if (value == L"warning" || value == L"warn") return 2;
	if (value == L"error") return 3;
	valid = false;
	return 1;
}

std::string WideToNarrow(const std::wstring& wstr) {
	if (wstr.empty()) return "";

	int sizeNeeded = WideCharToMultiByte(CP_UTF8, 0, &wstr[0], (int)wstr.size(), NULL, 0, NULL, NULL);
	std::string strTo(sizeNeeded, 0);
	WideCharToMultiByte(CP_UTF8, 0, &wstr[0], (int)wstr.size(), &strTo[0], sizeNeeded, NULL, NULL);
	return strTo;
}

// Called by the managed loader with a finished line (UTF-8, colors already applied).
void __stdcall LogMessage(const char* message) {
	ConsoleWriteLine(message);
}

void InitializeModLoader() {
	std::vector<std::wstring> launchArgs = GetLaunchArguments();

	if (HasArgument(launchArgs, L"--vanilla")) {
		return;
	}

	std::string modsPath = WideToNarrow(GetArgument(launchArgs, L"--mods-path", L"mods"));
	std::string modsConfigPath = WideToNarrow(GetArgument(launchArgs, L"--mods-config-path", L"modconfig"));


	// Create mods directory if it doesn't exist
	CreateDirectoryA(modsPath.c_str(), NULL);

	// Create modconfig directory if it doesn't exist
	CreateDirectoryA(modsConfigPath.c_str(), NULL);

	bool rich = !HasArgument(launchArgs, L"--plain-logs");
	bool gameLogs = HasArgument(launchArgs, L"--game-logs");
	bool validLogLevel = true;
	g_minLogLevel = ParseLogLevel(launchArgs, validLogLevel);
	SetUpConsole(rich, gameLogs);

	char banner[128];
	snprintf(banner, sizeof(banner), "        MIO Mod Loader v%d.%d.%d", MOD_LOADER_VERSION_MAJOR, MOD_LOADER_VERSION_MINOR, MOD_LOADER_VERSION_PATCH);
	ConsoleWriteLine("==============================================");
	ConsoleWriteLine(banner);
	ConsoleWriteLine("==============================================");
	if (!validLogLevel) {
		ConsoleWriteLine("[LOADER] --log-level must be followed by debug, info, warning or error; using info.");
	}

	// Disable DWM for GUI mods (needed on some systems)
	DisableDWM();

	HMODULE hModule = GetModuleHandleA("mio.exe");
	if (!hModule) {
		LogModLoaderMessage("ERROR: Failed to get mio.exe module handle!");
		return;
	}


	fs::path configPath = fs::current_path() / "mio-mod-loader" / "MioModLoader.runtimeconfig.json";
	fs::path assemblyPath = fs::current_path() / "mio-mod-loader" / "MioModLoader.dll";

	// Use the runtime shipped with the loader if it's there, otherwise an installed one.
	fs::path bundledDotNet = fs::current_path() / "mio-mod-loader" / "dotnet";
	bool useBundled = fs::exists(bundledDotNet / "host" / "fxr");

	get_hostfxr_parameters hostfxrParams{ sizeof(get_hostfxr_parameters), assemblyPath.c_str(), bundledDotNet.c_str() };
	char_t buffer[MAX_PATH];
	size_t bufferSize = sizeof(buffer) / sizeof(char_t);
	HMODULE lib = nullptr;
	if (get_hostfxr_path(buffer, &bufferSize, useBundled ? &hostfxrParams : nullptr) == 0) {
		lib = LoadLibraryW(buffer);
	}

	auto initFptr = lib ? (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(lib, "hostfxr_initialize_for_runtime_config") : nullptr;
	auto getDelegateFptr = lib ? (hostfxr_get_runtime_delegate_fn)GetProcAddress(lib, "hostfxr_get_runtime_delegate") : nullptr;
	auto closeFptr = lib ? (hostfxr_close_fn)GetProcAddress(lib, "hostfxr_close") : nullptr;
	if (!initFptr || !getDelegateFptr || !closeFptr) {
		NativeLog(useBundled ? "Couldn't load the bundled .NET runtime." : "Couldn't find an installed .NET runtime.");
		ShowDotNetError();
		return;
	}
	NativeLog(useBundled ? "Using the bundled .NET runtime." : "Using the installed .NET runtime.");

	hostfxr_initialize_parameters initParams{ sizeof(hostfxr_initialize_parameters), nullptr, bundledDotNet.c_str() };
	hostfxr_handle ctx = nullptr;
	int rc = initFptr(configPath.c_str(), useBundled ? &initParams : nullptr, &ctx);
	if (rc != 0 || ctx == nullptr) {
		char error[64];
		snprintf(error, sizeof(error), "Failed to initialize .NET (error 0x%08X).", (unsigned)rc);
		NativeLog(error);
		if (ctx != nullptr) closeFptr(ctx);
		ShowDotNetError();
		return;
	}

	void* loadAssembly = nullptr;
	rc = getDelegateFptr(ctx, hdt_load_assembly_and_get_function_pointer, &loadAssembly);
	if (rc != 0 || loadAssembly == nullptr) {
		NativeLog("Failed to retrieve runtime delegate");
		closeFptr(ctx);
		return;
	}

	auto loadAssemblyAndGetFunctionPointer = (load_assembly_and_get_function_pointer_fn)loadAssembly;
	void(*modInit)(void*, void*, void*, void*, int) = nullptr;

	rc = loadAssemblyAndGetFunctionPointer(
		assemblyPath.c_str(),
		L"MioModLoader.ModLoader, MioModLoader",
		L"LoadModsPointers",
		UNMANAGEDCALLERSONLY_METHOD,
		nullptr,
		(void**)&modInit
	);

	if (rc != 0 || modInit == nullptr) {
		NativeLog("Failed to resolve LoadMods entrypoint");
		closeFptr(ctx);
		return;
	}

	int logFlags = (g_rich ? LogFlags_Rich : 0) | (g_gameLogs ? LogFlags_GameLogs : 0) |
		(g_minLogLevel << LogFlags_LevelShift);
	modInit((void*)modsPath.c_str(), (void*)modsConfigPath.c_str(), &LogMessage, hModule, logFlags);
	closeFptr(ctx);
}