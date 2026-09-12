using HarmonyLib;

namespace ClassPixelPro.Platform.Windows;

public static class PatcherEntrance
{
    public static void InstallPatchers()
    {
        var harmony = new Harmony("cn.classpixelpro.app.patchers");
        harmony.PatchAll();
    }
}