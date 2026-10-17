// A genuine native PE that exits before confirming launcher startup. Used to
// prove that an unsuccessful updated process restores the original launcher.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
int WINAPI WinMain(HINSTANCE, HINSTANCE, LPSTR, int) { return 1; }
