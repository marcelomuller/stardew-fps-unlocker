using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace FpsUnlocker;

internal sealed class ModEntry : Mod
{
    public static ModConfig Config { get; private set; }

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();

        // The loop binds to MonoGame private members; if a game update renamed them, stay vanilla instead of crashing in Tick.
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(FrameLoop).TypeHandle);
        }
        catch (Exception ex)
        {
            Monitor.Log($"MonoGame internals changed, frame rate stays vanilla. Details: {ex.GetBaseException().Message}", LogLevel.Error);
            return;
        }

        var harmony = new Harmony(ModManifest.UniqueID);
        harmony.Patch(
            AccessTools.Method(typeof(Game), nameof(Game.Tick)),
            prefix: new HarmonyMethod(typeof(FrameLoop), nameof(FrameLoop.TickPrefix)) { priority = Priority.Last }
        );
        harmony.Patch(
            AccessTools.Method(typeof(TemporaryAnimatedSprite), nameof(TemporaryAnimatedSprite.Pool)),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterSpritePooled))
        );

        if (!FrameLoop.HooksSmapiRendering)
            Monitor.Log("Couldn't find SMAPI's render-event tick guard; mods that reset HUD state on Display.Rendering (e.g. UI Info Suite 2 icons) may flicker.", LogLevel.Warn);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.Display.RenderedHud += OnRenderedHud;
        helper.ConsoleCommands.Add("fps_stats", "Prints the current draw and update rates.", (_, _) =>
            Monitor.Log($"Draws/s: {FrameLoop.DrawsPerSecond}, updates/s: {FrameLoop.UpdatesPerSecond}, enabled: {Config.Enabled}, interpolate: {Config.Interpolate}, max FPS: {Config.MaxFps}, vsync: {Game1.options?.vsyncEnabled}", LogLevel.Info));

        Monitor.Log($"Loaded. Enabled={Config.Enabled}, MaxFps={Config.MaxFps}, Interpolate={Config.Interpolate}", LogLevel.Debug);
    }

    private static void AfterSpritePooled(TemporaryAnimatedSprite __instance)
    {
        Interpolator.ForgetSprite(__instance);
    }

    private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
    {
        var gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm == null)
            return;

        gmcm.Register(ModManifest, reset: () => Config = new ModConfig(), save: () => Helper.WriteConfig(Config));
        gmcm.AddBoolOption(ModManifest, () => Config.Enabled, v => Config.Enabled = v,
            () => "Unlock frame rate", () => "Draw as fast as vsync / the cap allows. Game speed stays at 60 ticks per second either way.");
        gmcm.AddNumberOption(ModManifest, () => Config.MaxFps, v => Config.MaxFps = v,
            () => "Max FPS", () => "0 = no cap: with vsync on, matches your monitor's refresh rate.",
            min: 0, max: 360, interval: 10, formatValue: v => v == 0 ? "Monitor (vsync)" : v.ToString());
        gmcm.AddBoolOption(ModManifest, () => Config.Interpolate, v => Config.Interpolate = v,
            () => "Smooth motion", () => "Blend the camera, characters, projectiles and particles between ticks. Without this the extra frames are identical copies.");
        gmcm.AddBoolOption(ModManifest, () => Config.ShowFpsCounter, v => Config.ShowFpsCounter = v,
            () => "Show FPS counter");
    }

    private void OnRenderedHud(object sender, RenderedHudEventArgs e)
    {
        if (!Config.ShowFpsCounter)
            return;
        string text = $"FPS {FrameLoop.DrawsPerSecond}  UPS {FrameLoop.UpdatesPerSecond}";
        Vector2 position = new(16, 16);
        e.SpriteBatch.DrawString(Game1.smallFont, text, position + new Vector2(2, 2), Color.Black * 0.6f);
        e.SpriteBatch.DrawString(Game1.smallFont, text, position, Color.White);
    }
}

public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string> tooltip = null, string fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string> tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string> formatValue = null, string fieldId = null);
}
