using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Projectiles;

namespace FpsUnlocker;

/// <summary>
/// Makes the frames drawn between two 60 Hz ticks show motion: before each update it snapshots the
/// camera and everything that moves in the current location; on each draw it temporarily moves them to
/// <c>lerp(previous tick, current tick, alpha)</c> and puts the real values back afterwards.
/// The cost is up to one tick (16.7 ms) of visual delay, the standard trade for fixed-step interpolation.
/// </summary>
internal static class Interpolator
{
    /// <summary>Movement per tick above this is treated as a teleport and drawn without blending. Fastest legit movement (horse + speed buffs) is ~10 px/tick.</summary>
    private const float CharacterSnapDistance = 48f;

    /// <summary>Projectiles, debris flying to the player and rain move much faster; wrap-arounds of screen-space rain/leaves are still far above this.</summary>
    private const float EffectSnapDistance = 128f;

    private const int ViewportSnapDistance = 96;

    /// <summary>
    /// The raw value behind a NetVector2 (<see cref="Character.Position"/>, projectile and debris positions, light sources).
    /// Writing it directly skips the NetField setter, so no change events fire and nothing is marked dirty for multiplayer sync.
    /// </summary>
    private static readonly AccessTools.FieldRef<NetFieldBase<Vector2, NetVector2>, Vector2> RawValue =
        AccessTools.FieldRefAccess<NetFieldBase<Vector2, NetVector2>, Vector2>("value");

    private static readonly Tracker<NetVector2> characters = new(f => RawValue(f), (f, v) => RawValue(f) = v, CharacterSnapDistance);
    private static readonly Tracker<NetVector2> netEffects = new(f => RawValue(f), (f, v) => RawValue(f) = v, EffectSnapDistance);
    private static readonly Tracker<TemporaryAnimatedSprite> sprites = new(s => s.position, (s, v) => s.position = v, EffectSnapDistance);
    private static readonly Tracker<Critter> critters = new(c => c.position, (c, v) => c.position = v, EffectSnapDistance);
    private static readonly Tracker<WeatherDebris> weatherDebris = new(d => d.position, (d, v) => d.position = v, EffectSnapDistance);
    private static readonly ITracker[] trackers = { characters, netEffects, sprites, critters, weatherDebris };

    private static GameLocation previousLocation;
    private static int previousViewportX, previousViewportY;
    private static bool hasSnapshot;

    private static bool viewportApplied;
    private static int realViewportX, realViewportY;

    // Rain is an array of structs, so it's tracked by index.
    private static RainDrop[] previousRainArray;
    private static Vector2[] previousRain = Array.Empty<Vector2>();
    private static Vector2[] realRain = Array.Empty<Vector2>();
    private static bool rainApplied;

    private static Vector2 previousSnowPos, realSnowPos;
    private static bool snowApplied;

    private static bool restorePerTickState;
    private static float savedFlashAlpha;
    private static int savedMouseFadeTimer;

    /// <summary>Called right before every game update.</summary>
    public static void BeforeUpdate()
    {
        foreach (ITracker tracker in trackers)
            tracker.Clear();
        previousRainArray = null;
        hasSnapshot = false;

        GameLocation location = Game1.currentLocation;
        if (!ModEntry.Config.Interpolate || !Context.IsWorldReady || Context.IsSplitScreen || location == null)
            return;

        previousLocation = location;
        previousViewportX = Game1.viewport.X;
        previousViewportY = Game1.viewport.Y;

        characters.Snapshot(Game1.player.position.Field);
        foreach (Farmer farmer in location.farmers)
            characters.Snapshot(farmer.position.Field);
        foreach (NPC npc in location.characters)
            characters.Snapshot(npc.position.Field);
        foreach (FarmAnimal animal in location.animals.Values)
            characters.Snapshot(animal.position.Field);

        foreach (Projectile projectile in location.projectiles)
            netEffects.Snapshot(projectile.position.Field);
        foreach (Debris debris in location.debris)
        {
            foreach (Chunk chunk in debris.Chunks)
                netEffects.Snapshot(chunk.position.Field);
        }
        if (Game1.currentLightSources != null)
        {
            foreach (LightSource light in Game1.currentLightSources.Values)
                netEffects.Snapshot(light.position);
        }

        SnapshotSprites(location.temporarySprites);
        SnapshotSprites(Game1.screenOverlayTempSprites);
        SnapshotSprites(Game1.uiOverlayTempSprites);
        if (location.critters != null)
        {
            foreach (Critter critter in location.critters)
                critters.Snapshot(critter);
        }
        if (Game1.debrisWeather != null)
        {
            foreach (WeatherDebris debris in Game1.debrisWeather)
                weatherDebris.Snapshot(debris);
        }

        if (Game1.rainDrops != null)
        {
            previousRainArray = Game1.rainDrops;
            if (previousRain.Length != previousRainArray.Length)
                previousRain = new Vector2[previousRainArray.Length];
            for (int i = 0; i < previousRainArray.Length; i++)
                previousRain[i] = previousRainArray[i].position;
        }
        previousSnowPos = Game1.snowPos;

        hasSnapshot = true;
    }

    /// <summary>A temporary sprite went back to the pool and may be reused as an unrelated sprite this tick; don't blend it.</summary>
    public static void ForgetSprite(TemporaryAnimatedSprite sprite)
    {
        sprites.Forget(sprite);
    }

    /// <param name="alpha">How far real time is between the last tick and the next one, 0..1.</param>
    /// <param name="updatedThisFrame">Whether at least one update ran since the previous draw.</param>
    public static void BeginDraw(float alpha, bool updatedThisFrame)
    {
        // A few things are decremented inside Draw instead of Update; on extra frames, undo that so they keep vanilla speed.
        restorePerTickState = !updatedThisFrame;
        if (restorePerTickState)
        {
            savedFlashAlpha = Game1.flashAlpha;
            savedMouseFadeTimer = Game1.timerUntilMouseFade;
        }

        if (!hasSnapshot || Game1.currentLocation != previousLocation || Context.IsSplitScreen)
            return;
        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha >= 1f)
            return;

        foreach (ITracker tracker in trackers)
            tracker.Apply(alpha);

        int dx = Game1.viewport.X - previousViewportX;
        int dy = Game1.viewport.Y - previousViewportY;
        if ((dx != 0 || dy != 0) && Math.Abs(dx) <= ViewportSnapDistance && Math.Abs(dy) <= ViewportSnapDistance)
        {
            realViewportX = Game1.viewport.X;
            realViewportY = Game1.viewport.Y;
            Game1.viewport.X = previousViewportX + (int)MathF.Round(dx * alpha);
            Game1.viewport.Y = previousViewportY + (int)MathF.Round(dy * alpha);
            viewportApplied = true;
        }

        RainDrop[] rain = Game1.rainDrops;
        if (rain != null && rain == previousRainArray)
        {
            if (realRain.Length != rain.Length)
                realRain = new Vector2[rain.Length];
            for (int i = 0; i < rain.Length; i++)
            {
                Vector2 current = realRain[i] = rain[i].position;
                Vector2 delta = current - previousRain[i];
                if (delta.LengthSquared() <= EffectSnapDistance * EffectSnapDistance)
                    rain[i].position = previousRain[i] + delta * alpha;
            }
            rainApplied = true;
        }

        Vector2 snowDelta = Game1.snowPos - previousSnowPos;
        if (snowDelta != Vector2.Zero && snowDelta.LengthSquared() <= EffectSnapDistance * EffectSnapDistance)
        {
            realSnowPos = Game1.snowPos;
            Game1.snowPos = previousSnowPos + snowDelta * alpha;
            snowApplied = true;
        }
    }

    public static void EndDraw()
    {
        foreach (ITracker tracker in trackers)
            tracker.Restore();

        if (viewportApplied)
        {
            Game1.viewport.X = realViewportX;
            Game1.viewport.Y = realViewportY;
            viewportApplied = false;
        }

        if (rainApplied)
        {
            RainDrop[] rain = Game1.rainDrops;
            if (rain != null && rain.Length == realRain.Length)
            {
                for (int i = 0; i < rain.Length; i++)
                    rain[i].position = realRain[i];
            }
            rainApplied = false;
        }

        if (snowApplied)
        {
            Game1.snowPos = realSnowPos;
            snowApplied = false;
        }

        if (restorePerTickState)
        {
            Game1.flashAlpha = savedFlashAlpha;
            Game1.timerUntilMouseFade = savedMouseFadeTimer;
            restorePerTickState = false;
        }
    }

    private static void SnapshotSprites(TemporaryAnimatedSpriteList list)
    {
        if (list == null)
            return;
        foreach (TemporaryAnimatedSprite sprite in list)
            sprites.Snapshot(sprite);
    }

    private interface ITracker
    {
        void Clear();
        void Apply(float alpha);
        void Restore();
    }

    /// <summary>Previous-tick positions for one kind of object, plus the real values to put back after a draw.</summary>
    private sealed class Tracker<T> : ITracker where T : class
    {
        private readonly Func<T, Vector2> get;
        private readonly Action<T, Vector2> set;
        private readonly float snapDistanceSquared;
        private readonly Dictionary<T, Vector2> previous = new(ReferenceEqualityComparer.Instance);
        private readonly List<(T Item, Vector2 Real)> applied = new();

        public Tracker(Func<T, Vector2> get, Action<T, Vector2> set, float snapDistance)
        {
            this.get = get;
            this.set = set;
            snapDistanceSquared = snapDistance * snapDistance;
        }

        public void Snapshot(T item)
        {
            if (item != null)
                previous[item] = get(item);
        }

        public void Forget(T item)
        {
            previous.Remove(item);
        }

        public void Clear()
        {
            previous.Clear();
        }

        public void Apply(float alpha)
        {
            foreach ((T item, Vector2 before) in previous)
            {
                Vector2 current = get(item);
                Vector2 delta = current - before;
                if (delta == Vector2.Zero || delta.LengthSquared() > snapDistanceSquared)
                    continue;
                applied.Add((item, current));
                set(item, before + delta * alpha);
            }
        }

        public void Restore()
        {
            foreach ((T item, Vector2 real) in applied)
                set(item, real);
            applied.Clear();
        }
    }
}
