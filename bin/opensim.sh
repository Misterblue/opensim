#!/bin/sh
rm -f OpenSim.log
ulimit -s 1048576
dotnet OpenSim.dll
