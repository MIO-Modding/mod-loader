rd /s /q output
md output
copy /y "%cd%\bin\Release\x64\winhttp.dll" "output"
md %cd%\output\mio-mod-loader
copy /y "%cd%\mio-mod-loader\bin\Release\net8.0\MioModLoader.dll" "%cd%\output\mio-mod-loader"
copy /y "%cd%\mio-mod-loader\bin\Release\net8.0\MioModLoader.runtimeconfig.json" "%cd%\output\mio-mod-loader"
copy /y "%cd%\mio-mod-loader\bin\Release\net8.0\PolyHook2.Net.dll" "%cd%\output\mio-mod-loader"
xcopy "%cd%\mio-mod-loader\bin\Release\net8.0\runtimes\win-x64" "%cd%\output\mio-mod-loader\runtimes\win-x64" /E /I /H /C
copy /y "%cd%\libs\MioBinds.dll" "%cd%\output\mio-mod-loader"
copy /y "%cd%\libs\hostfxr.dll" "%cd%\output\mio-mod-loader"
copy /y "%cd%\INSTALL.txt" "%cd%\output"
