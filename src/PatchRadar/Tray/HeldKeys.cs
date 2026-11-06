using System.Runtime.InteropServices;

namespace PatchRadar.Tray;

/// <summary>Reads the keyboard directly: a tray menu does not reliably get WPF keyboard state.</summary>
public static class HeldKeys
{
    private const int VkShift = 0x10, VkMenu = 0x12;   // VK_MENU is Alt

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    public static bool ShiftOrAlt() => Down(VkShift) || Down(VkMenu);

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
}
