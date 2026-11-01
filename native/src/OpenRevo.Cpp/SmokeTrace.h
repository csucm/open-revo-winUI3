#pragma once
#include <filesystem>
#include <fstream>
namespace openrevo {
inline std::filesystem::path smokeTracePath;
inline void smokeTrace(char const* stage) {
    if(!smokeTracePath.empty()){std::ofstream out(smokeTracePath,std::ios::app);out<<stage<<'\n';}
}
}
