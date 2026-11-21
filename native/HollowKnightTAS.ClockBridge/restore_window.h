#ifndef HKTAS_RESTORE_WINDOW_H
#define HKTAS_RESTORE_WINDOW_H
#include <stdio.h>

/* Presentation only: Unity keeps executing and rendering normally. The controller
 * shows the HWND under its captured image using its own, unhooked USER32 imports.
 * Once the controller sets this property all calls pass through unchanged. */
#define HKTAS_RESTORE_VISIBLE_PROPERTY L"HKTAS.RestorePresentationReady"

static BOOL restore_window_held(HWND window)
{
    wchar_t name[64];
    return GetClassNameW(window, name, 64) && lstrcmpW(name, L"UnityWndClass") == 0
        && GetPropW(window, HKTAS_RESTORE_VISIBLE_PROPERTY) == NULL;
}

static HWND WINAPI restore_create_window(DWORD extended, LPCWSTR class_name,
    LPCWSTR title, DWORD style, int x, int y, int width, int height,
    HWND parent, HMENU menu, HINSTANCE instance, LPVOID parameter)
{
    wchar_t atom_name[64];
    LPCWSTR resolved = class_name;
    if (IS_INTRESOURCE(class_name)) {
        WNDCLASSEXW info = {0};
        info.cbSize = sizeof(info);
        if (GetClassInfoExW(instance, class_name, &info)) resolved = info.lpszClassName;
        if (IS_INTRESOURCE(resolved)) {
            if (GlobalGetAtomNameW((ATOM)(ULONG_PTR)class_name, atom_name, 64)) resolved = atom_name;
        }
    }
    if (resolved && !IS_INTRESOURCE(resolved) && lstrcmpW(resolved, L"UnityWndClass") == 0)
        style &= ~WS_VISIBLE;
    return CreateWindowExW(extended, class_name, title, style, x, y, width, height,
        parent, menu, instance, parameter);
}

static BOOL WINAPI restore_show_window(HWND window, int command)
{
    if (restore_window_held(window)) return IsWindowVisible(window);
    return ShowWindow(window, command);
}

static BOOL WINAPI restore_position_window(HWND window, HWND after, int x, int y,
    int width, int height, UINT flags)
{
    if (restore_window_held(window)) {
        flags &= ~(SWP_SHOWWINDOW | SWP_HIDEWINDOW);
        flags |= SWP_NOACTIVATE | SWP_NOZORDER;
    }
    return SetWindowPos(window, after, x, y, width, height, flags);
}

static BOOL WINAPI restore_foreground_window(HWND window)
{
    return restore_window_held(window) ? FALSE : SetForegroundWindow(window);
}

static LONG_PTR restore_style(HWND window, int index, LONG_PTR value)
{
    if (!restore_window_held(window)) return value;
    if (index == GWL_STYLE)
        return (value & ~WS_VISIBLE) | (GetWindowLongPtrW(window, index) & WS_VISIBLE);
    if (index == GWL_EXSTYLE)
        return value | (GetWindowLongPtrW(window, index) & WS_EX_NOACTIVATE);
    return value;
}

static LONG_PTR WINAPI restore_window_long_w(HWND window, int index, LONG_PTR value)
{ return SetWindowLongPtrW(window, index, restore_style(window, index, value)); }
static LONG_PTR WINAPI restore_window_long_a(HWND window, int index, LONG_PTR value)
{ return SetWindowLongPtrA(window, index, restore_style(window, index, value)); }
static LONG WINAPI restore_window_long_a32(HWND window, int index, LONG value)
{ return SetWindowLongA(window, index, (LONG)restore_style(window, index, value)); }

static BOOL install_restore_window_hooks(void)
{
    wchar_t enabled[2];
    if (GetEnvironmentVariableW(L"HKTAS_RESTORE_HIDDEN_WINDOW", enabled, 2) != 1
        || enabled[0] != L'1') return TRUE;
    struct { const char *name; void *hook; BOOL found; } hooks[] = {
        {"CreateWindowExW", (void *)restore_create_window, FALSE},
        {"ShowWindow", (void *)restore_show_window, FALSE},
        {"SetWindowPos", (void *)restore_position_window, FALSE},
        {"SetForegroundWindow", (void *)restore_foreground_window, FALSE},
        {"SetWindowLongPtrW", (void *)restore_window_long_w, FALSE},
        {"SetWindowLongPtrA", (void *)restore_window_long_a, FALSE},
        {"SetWindowLongA", (void *)restore_window_long_a32, FALSE}
    };
    BYTE *base = (BYTE *)GetModuleHandleW(L"UnityPlayer.dll");
    if (!base) {
        OutputDebugStringA("[HKTAS][WARN] restore_window: UnityPlayer.dll module not found; skipping USER32 IAT hooks.\n");
        return FALSE;
    }
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        OutputDebugStringA("[HKTAS][WARN] restore_window: UnityPlayer.dll invalid DOS header; skipping USER32 IAT hooks.\n");
        return FALSE;
    }
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE
        || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
        OutputDebugStringA("[HKTAS][WARN] restore_window: UnityPlayer.dll invalid NT64 header; skipping USER32 IAT hooks.\n");
        return FALSE;
    }
    IMAGE_DATA_DIRECTORY directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!directory.VirtualAddress || directory.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR)) {
        OutputDebugStringA("[HKTAS][WARN] restore_window: UnityPlayer.dll missing import directory; skipping USER32 IAT hooks.\n");
        return FALSE;
    }
    IMAGE_IMPORT_DESCRIPTOR *imports = (IMAGE_IMPORT_DESCRIPTOR *)(base + directory.VirtualAddress);
    for (; imports->Name; ++imports) {
        if (lstrcmpiA((const char *)(base + imports->Name), "USER32.dll") != 0
            || !imports->OriginalFirstThunk || !imports->FirstThunk) continue;
        IMAGE_THUNK_DATA64 *names = (IMAGE_THUNK_DATA64 *)(base + imports->OriginalFirstThunk);
        IMAGE_THUNK_DATA64 *addresses = (IMAGE_THUNK_DATA64 *)(base + imports->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++addresses) {
            if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal)) continue;
            IMAGE_IMPORT_BY_NAME *name = (IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData);
            for (unsigned i = 0; i < sizeof(hooks) / sizeof(hooks[0]); ++i) {
                if (lstrcmpA((const char *)name->Name, hooks[i].name) != 0) continue;
                DWORD previous, ignored;
                if (!VirtualProtect(&addresses->u1.Function, sizeof(void *), PAGE_READWRITE, &previous)) {
                    char msg[160];
                    snprintf(msg, sizeof(msg), "[HKTAS][WARN] restore_window: VirtualProtect failed for %s; skipping.\n", hooks[i].name);
                    OutputDebugStringA(msg);
                    continue;
                }
                InterlockedExchangePointer((PVOID volatile *)&addresses->u1.Function, hooks[i].hook);
                if (!VirtualProtect(&addresses->u1.Function, sizeof(void *), previous, &ignored)) {
                    char msg[160];
                    snprintf(msg, sizeof(msg), "[HKTAS][WARN] restore_window: VirtualProtect restore failed for %s.\n", hooks[i].name);
                    OutputDebugStringA(msg);
                }
                hooks[i].found = TRUE;
            }
        }
    }
    for (unsigned i = 0; i < sizeof(hooks) / sizeof(hooks[0]); ++i) {
        if (!hooks[i].found) {
            char msg[160];
            snprintf(msg, sizeof(msg), "[HKTAS][WARN] restore_window: USER32 import '%s' not found; skipping hook.\n", hooks[i].name);
            OutputDebugStringA(msg);
        }
    }
    return TRUE;
}
#endif
