rd /s /q output
md output
copy /y "%cd%\bin\Release\x64\winhttp.dll" "output"
md %cd%\output\mio-mod-loader
copy /y "%cd%\mio-mod-loader\bin\Release\net8.0\MioModLoader.dll" "%cd%\output\mio-mod-loader"
copy /y "%cd%\mio-mod-loader\bin\Release\net8.0\MioModLoader.runtimeconfig.json" "%cd%\output\mio-mod-loader"