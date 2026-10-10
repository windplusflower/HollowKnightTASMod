// Generated from all 13 pure Phase 0 boundaries. Bindings only;
// state/action/variable coverage is a separate carry codec requirement.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    public static class CanonicalHeroFsmBindingRegistry
    {
        private static readonly Dictionary<string, string> Bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Knight[0]/Attacks[0]/AltSlash[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/AltSlash[0]:PlayMakerFSM[1]", "nail_cancel_check" },
            { "Knight[0]/Attacks[0]/Cyclone Slash[0]/Hits[0]/Hit L[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/Cyclone Slash[0]/Hits[0]/Hit L[0]:PlayMakerFSM[1]", "nailart_damage" },
            { "Knight[0]/Attacks[0]/Cyclone Slash[0]/Hits[0]/Hit R[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/Cyclone Slash[0]/Hits[0]/Hit R[0]:PlayMakerFSM[1]", "nailart_damage" },
            { "Knight[0]/Attacks[0]/Cyclone Slash[0]:PlayMakerFSM[0]", "Control Collider" },
            { "Knight[0]/Attacks[0]/Dash Slash[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/Dash Slash[0]:PlayMakerFSM[1]", "Control Collider" },
            { "Knight[0]/Attacks[0]/Dash Slash[0]:PlayMakerFSM[2]", "nailart_damage" },
            { "Knight[0]/Attacks[0]/DownSlash[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/DownSlash[0]:PlayMakerFSM[1]", "nail_cancel_check" },
            { "Knight[0]/Attacks[0]/Great Slash[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/Great Slash[0]:PlayMakerFSM[1]", "Control Collider" },
            { "Knight[0]/Attacks[0]/Great Slash[0]:PlayMakerFSM[2]", "nailart_damage" },
            { "Knight[0]/Attacks[0]/Sharp Shadow[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/Slash[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/Slash[0]:PlayMakerFSM[1]", "nail_cancel_check" },
            { "Knight[0]/Attacks[0]/UpSlash[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/UpSlash[0]:PlayMakerFSM[1]", "nail_cancel_check" },
            { "Knight[0]/Attacks[0]/WallSlash[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Attacks[0]/WallSlash[0]:PlayMakerFSM[1]", "nail_cancel_check" },
            { "Knight[0]/Attacks[0]:PlayMakerFSM[0]", "Set Slash Damage" },
            { "Knight[0]/Attacks[0]:PlayMakerFSM[1]", "Set Sharp Shadow Damage" },
            { "Knight[0]/Blessing Ghost[0]:PlayMakerFSM[0]", "Blessing Control" },
            { "Knight[0]/Can Focus Particles[0]:PlayMakerFSM[0]", "Play" },
            { "Knight[0]/Charm Effects[0]/Blocker Shield[0]/Pusher[0]/Hit L[0]:PlayMakerFSM[0]", "push_enemy" },
            { "Knight[0]/Charm Effects[0]/Blocker Shield[0]/Pusher[0]/Hit R[0]:PlayMakerFSM[0]", "push_enemy" },
            { "Knight[0]/Charm Effects[0]/Blocker Shield[0]/Pusher[0]/Hit U[0]:PlayMakerFSM[0]", "push_enemy" },
            { "Knight[0]/Charm Effects[0]/Blocker Shield[0]/Pusher[0]:PlayMakerFSM[0]", "Deactivate" },
            { "Knight[0]/Charm Effects[0]/Blocker Shield[0]:PlayMakerFSM[0]", "Control" },
            { "Knight[0]/Charm Effects[0]/Dung[0]:PlayMakerFSM[0]", "Control" },
            { "Knight[0]/Charm Effects[0]/Fury[0]:PlayMakerFSM[0]", "Control Audio" },
            { "Knight[0]/Charm Effects[0]/Rage Burst Effect[0]:PlayMakerFSM[0]", "Deactivate" },
            { "Knight[0]/Charm Effects[0]/Thorn Hit[0]/Hit D[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Charm Effects[0]/Thorn Hit[0]/Hit D[0]:PlayMakerFSM[1]", "set_thorn_damage" },
            { "Knight[0]/Charm Effects[0]/Thorn Hit[0]/Hit L[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Charm Effects[0]/Thorn Hit[0]/Hit L[0]:PlayMakerFSM[1]", "set_thorn_damage" },
            { "Knight[0]/Charm Effects[0]/Thorn Hit[0]/Hit R[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Charm Effects[0]/Thorn Hit[0]/Hit R[0]:PlayMakerFSM[1]", "set_thorn_damage" },
            { "Knight[0]/Charm Effects[0]/Thorn Hit[0]/Hit U[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Charm Effects[0]/Thorn Hit[0]/Hit U[0]:PlayMakerFSM[1]", "set_thorn_damage" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[0]", "Thorn Counter" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[10]", "Pool Flukes" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[11]", "Pool Flukes" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[1]", "Fury" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[2]", "Enemy Recoil Up" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[3]", "Hatchling Spawn" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[4]", "Slash Size Modifiers" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[5]", "Set Spell Cost" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[6]", "White Charm" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[7]", "Spawn Orbit Shield" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[8]", "Weaverling Control" },
            { "Knight[0]/Charm Effects[0]:PlayMakerFSM[9]", "Spawn Grimmchild" },
            { "Knight[0]/Dream Effects[0]/DG Set Impact[0]:PlayMakerFSM[0]", "deactivate" },
            { "Knight[0]/Dream Effects[0]/DG Set Ready[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Dream Effects[0]/DG Set Release[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Dream Effects[0]/Hitbox[0]:PlayMakerFSM[0]", "Deactivate" },
            { "Knight[0]/Dream Effects[0]/Hitbox[0]:PlayMakerFSM[1]", "Send Event" },
            { "Knight[0]/Effects[0]/Acid Armour[0]:PlayMakerFSM[0]", "Control" },
            { "Knight[0]/Effects[0]/Damage Effect[0]/low health hit effect[0]:PlayMakerFSM[0]", "Deactivate After Delay" },
            { "Knight[0]/Effects[0]/Damage Effect[0]:PlayMakerFSM[0]", "Knight Damage" },
            { "Knight[0]/Effects[0]/Dash Burst[0]:PlayMakerFSM[0]", "Effect Control" },
            { "Knight[0]/Effects[0]/Fall Trail[0]:PlayMakerFSM[0]", "Trail Control" },
            { "Knight[0]/Effects[0]/SD Burst Glow[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/SD Burst[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/SD Burst[0]:PlayMakerFSM[1]", "Damage Control" },
            { "Knight[0]/Effects[0]/SD Burst[0]:PlayMakerFSM[2]", "damages_enemy" },
            { "Knight[0]/Effects[0]/SD Charge Glow[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/SD Charge Wall[0]:PlayMakerFSM[0]", "color_fader" },
            { "Knight[0]/Effects[0]/SD Charge[0]:PlayMakerFSM[0]", "color_fader" },
            { "Knight[0]/Effects[0]/SD Crystal Gen G1[0]/Floor Check[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/SD Crystal Gen G1[0]:PlayMakerFSM[0]", "superdash_crystal_gen" },
            { "Knight[0]/Effects[0]/SD Crystal Gen G1[0]:PlayMakerFSM[1]", "Spawn" },
            { "Knight[0]/Effects[0]/SD Crystal Gen G2[0]/Floor Check[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/SD Crystal Gen G2[0]:PlayMakerFSM[0]", "superdash_crystal_gen" },
            { "Knight[0]/Effects[0]/SD Crystal Gen G2[0]:PlayMakerFSM[1]", "Spawn" },
            { "Knight[0]/Effects[0]/SD Crystal Gen W1[0]/Floor Check[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/SD Crystal Gen W1[0]:PlayMakerFSM[0]", "superdash_crystal_gen" },
            { "Knight[0]/Effects[0]/SD Crystal Gen W1[0]:PlayMakerFSM[1]", "Spawn" },
            { "Knight[0]/Effects[0]/SD Crystal Gen W2[0]/Floor Check[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/SD Crystal Gen W2[0]:PlayMakerFSM[0]", "superdash_crystal_gen" },
            { "Knight[0]/Effects[0]/SD Crystal Gen W2[0]:PlayMakerFSM[1]", "Spawn" },
            { "Knight[0]/Effects[0]/SD Trail[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/Shadow Burst Down[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/Shadow Burst[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/Shadow Recharge[0]:PlayMakerFSM[0]", "Recharge Effect" },
            { "Knight[0]/Effects[0]/Shadow Ring[0]:PlayMakerFSM[0]", "Play Effect" },
            { "Knight[0]/Effects[0]/Soul Burst[0]:PlayMakerFSM[0]", "Rotate" },
            { "Knight[0]/Effects[0]/Soul Burst[0]:PlayMakerFSM[1]", "Deactivate" },
            { "Knight[0]/Effects[0]/Wall Hit Effect[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/Wall Puff[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Effects[0]/White Flash DJ[0]:PlayMakerFSM[0]", "fade and deactivate" },
            { "Knight[0]/Effects[0]/White Flash NA[0]:PlayMakerFSM[0]", "fade and deactivate" },
            { "Knight[0]/Hero Death[0]:PlayMakerFSM[0]", "Hero Death Anim" },
            { "Knight[0]/HeroBox[0]/Geo Get[0]:PlayMakerFSM[0]", "Control" },
            { "Knight[0]/HeroBox[0]:PlayMakerFSM[0]", "Geo Get" },
            { "Knight[0]/HeroLight[0]:PlayMakerFSM[0]", "HeroLight Control" },
            { "Knight[0]/HeroLight[0]:PlayMakerFSM[1]", "color_fader" },
            { "Knight[0]/Spells[0]/Q Charge[0]:PlayMakerFSM[0]", "color_fader" },
            { "Knight[0]/Spells[0]/Q Fall Damage[0]:PlayMakerFSM[0]", "Set Damage" },
            { "Knight[0]/Spells[0]/Q Fall Damage[0]:PlayMakerFSM[1]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Q Mega[0]/Hit L[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Q Mega[0]/Hit R[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Q Mega[0]:PlayMakerFSM[0]", "Hit Box Control" },
            { "Knight[0]/Spells[0]/Q Orbs 2[0]:PlayMakerFSM[0]", "Play" },
            { "Knight[0]/Spells[0]/Q Orbs[0]:PlayMakerFSM[0]", "Play" },
            { "Knight[0]/Spells[0]/Q Pillar[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Spells[0]/Q Slam 2[0]/Hit L[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Q Slam 2[0]/Hit L[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Q Slam 2[0]/Hit R[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Q Slam 2[0]/Hit R[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Q Slam 2[0]:PlayMakerFSM[0]", "Hit Box Control" },
            { "Knight[0]/Spells[0]/Q Slam Lines[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Spells[0]/Q Slam[0]/Hit L[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Q Slam[0]/Hit L[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Q Slam[0]/Hit R[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Q Slam[0]/Hit R[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Q Slam[0]:PlayMakerFSM[0]", "Hit Box Control" },
            { "Knight[0]/Spells[0]/Q Trail 2[0]:PlayMakerFSM[0]", "color_fader" },
            { "Knight[0]/Spells[0]/Q Trail[0]:PlayMakerFSM[0]", "color_fader" },
            { "Knight[0]/Spells[0]/Q1 Pillar[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Spells[0]/Scr Base 2[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Spells[0]/Scr Base[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Spells[0]/Scr Heads 2[0]/Hit L[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Scr Heads 2[0]/Hit L[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Scr Heads 2[0]/Hit R[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Scr Heads 2[0]/Hit R[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Scr Heads 2[0]/Hit U[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Scr Heads 2[0]/Hit U[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Scr Heads 2[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Spells[0]/Scr Heads 2[0]:PlayMakerFSM[1]", "Deactivate on Hit" },
            { "Knight[0]/Spells[0]/Scr Heads[0]/Hit L[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Scr Heads[0]/Hit L[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Scr Heads[0]/Hit R[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Scr Heads[0]/Hit R[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Scr Heads[0]/Hit U[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Spells[0]/Scr Heads[0]/Hit U[0]:PlayMakerFSM[1]", "Set Damage" },
            { "Knight[0]/Spells[0]/Scr Heads[0]:PlayMakerFSM[0]", "Hit Box Control" },
            { "Knight[0]/Spells[0]/Scr Heads[0]:PlayMakerFSM[1]", "FSM" },
            { "Knight[0]/Spells[0]/Scr Heads[0]:PlayMakerFSM[2]", "Deactivate on Hit" },
            { "Knight[0]/Spells[0]/Scr Orbs 2[0]:PlayMakerFSM[0]", "Play" },
            { "Knight[0]/Spells[0]/Scr Orbs[0]:PlayMakerFSM[0]", "Play" },
            { "Knight[0]/SuperDash Damage[0]:PlayMakerFSM[0]", "damages_enemy" },
            { "Knight[0]/Vignette[0]/Darkness Plates[0]/Darkness BG[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Vignette[0]/Darkness Plates[0]/Darkness FG[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]/Vignette[0]:PlayMakerFSM[0]", "Darkness Control" },
            { "Knight[0]/White_Flower_Break[0]/white_fader[0]:PlayMakerFSM[0]", "FSM" },
            { "Knight[0]:PlayMakerFSM[0]", "ProxyFSM" },
            { "Knight[0]:PlayMakerFSM[10]", "Globalise" },
            { "Knight[0]:PlayMakerFSM[11]", "Control Interpolation" },
            { "Knight[0]:PlayMakerFSM[1]", "Spell Control" },
            { "Knight[0]:PlayMakerFSM[2]", "Map Control" },
            { "Knight[0]:PlayMakerFSM[3]", "Roar Lock" },
            { "Knight[0]:PlayMakerFSM[4]", "Superdash" },
            { "Knight[0]:PlayMakerFSM[5]", "Nail Arts" },
            { "Knight[0]:PlayMakerFSM[6]", "Surface Water" },
            { "Knight[0]:PlayMakerFSM[7]", "Dream Nail" },
            { "Knight[0]:PlayMakerFSM[8]", "Dream Return" },
            { "Knight[0]:PlayMakerFSM[9]", "Spore Cooldown" },
        };
        public const int Count = 160;
        public static bool IsCanonicalVisual(string path, string name) =>
            path != null && Bindings.TryGetValue(path, out var expected) && expected == name
            && path.StartsWith("Knight[0]/HeroLight[0]:PlayMakerFSM[", StringComparison.Ordinal)
            && (name == "HeroLight Control" || name == "color_fader");

        public static void Validate(IEnumerable<KeyValuePair<string, string>> actual)
        {
            if (actual == null) throw new ArgumentNullException(nameof(actual));
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in actual)
            {
                if (found.Count >= Count || entry.Key == null || entry.Value == null || found.ContainsKey(entry.Key)
                    || !Bindings.TryGetValue(entry.Key, out var expected) || expected != entry.Value)
                    throw new InvalidOperationException("Canonical Hero FSM binding differs: " + entry.Key);
                found.Add(entry.Key, entry.Value);
            }
            if (found.Count != Count)
                throw new InvalidOperationException("Canonical Hero FSM binding is missing: "
                    + Bindings.Keys.First(path => !found.ContainsKey(path)));
        }

        public static IReadOnlyDictionary<string, string> Snapshot() => new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(Bindings, StringComparer.Ordinal));
    }
}
