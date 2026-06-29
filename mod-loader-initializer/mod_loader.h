#pragma once

// Mod Loader Version
#define MOD_LOADER_VERSION_MAJOR 0
#define MOD_LOADER_VERSION_MINOR 0
#define MOD_LOADER_VERSION_PATCH 1

typedef struct Version {
	int major, minor, patch;
} Version;

inline Version make_APIVersion(int major, int minor, int patch) {
	Version v = { major, minor, patch };
	return v;
}

extern "C" {
	void InitializeModLoader();
	bool IsTargetExecutable();
	bool IsCorrectRun();
	void DisableDWM();
	void LogModLoaderMessage(const char* message);
	Version GetModLoaderVersion();
}