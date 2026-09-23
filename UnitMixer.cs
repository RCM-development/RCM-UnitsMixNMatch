
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using Microsoft.Win32;
using TestMod;
using UnityEngine;
using UnityEngine.Profiling;

namespace RCM_UnitsMixNMatch
{

    [BepInDependency(RCMManager.IDENTIFIER, BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(IDENTIFIER, "Units Mix & Match", "1.0.0.0")]
    public class UnitMixer : BaseUnityPlugin
    {
        const string IDENTIFIER = "RCM.plugins.mixnmatch";
        static RCMModUI mod;
        private void Awake()
        {
            VerboseLog = Config.Bind("Diagnostics", "VerboseLog", false,
                "Log the per-unit measurements behind each mounting decision (structural check lines). For tuning; noisy.").Value;
            LoadEntityCompatibilityList();
            new Harmony(IDENTIFIER).PatchAll();
            RCMManager.ConnectMod("Units Mix&Match").ContinueWith(t =>
            {
                mod = t.Result;

                mod.CreateButtonField("Reload unit compat txt", LoadEntityCompatibilityList);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        const string supported_entities_path = "BepInEx\\plugins\\MixNMatchUnits.txt";
        static HashSet<string> supported_entities = new HashSet<string>();

        // hook for other mods (e.g. RCM_Randomizer): return a donor entityId for the given base entity,
        // null to fall back to the built-in per-spawn random pick, or "" to skip mixing this
        // entity entirely (e.g. neutral wildlife). selections outside the compat list are ignored
        // so external mods can't bypass MixNMatchUnits.txt
        public static Func<string, string> DonorSelector;
        public static IReadOnlyCollection<string> SupportedEntities => supported_entities;

        // scale transplanted turrets so their footprint roughly matches the turret they replace
        public static bool ScaleTransplantedTurrets = true;

        // hitch attribution: log any single swap slower than this (0 disables)
        public static double LogSwapsSlowerThanMs = 3.0;

        // Per-unit measurement lines ("structural check ...") are how mounting thresholds get tuned,
        // and they are two thirds of a normal session's log - one line per unit in the compat list,
        // at menu time. Off for players, one config switch away for whoever tunes.
        public static bool VerboseLog;
        void LoadEntityCompatibilityList(){
            supported_entities.Clear();
            if (File.Exists(supported_entities_path)){
                string line = File.ReadAllText(supported_entities_path);
                // Split by tab
                string[] parts = line.Split('\t');
                foreach (string s in parts) supported_entities.Add(s);
            }
            else RCMManager.Log("LoadEntityCompatibilityList: no entities list to pull valid entities from");
        }

        // What an entity can do in a swap, answered UP FRONT for external donor selectors (the
        // randomizer's seeded map). A pairing the swap would refuse anyway must never be made:
        // the card gets named after it, the swap silently falls back to stock, and the name, the
        // preview and the spawned unit all disagree.
        //   CanDonate  - has a usable aiming pivot that is not its own torso (buildings exempt).
        //   CanReceive - has a pivot, and is not a RANGED unit whose pivot is its own body. Such a
        //                unit (the turretless T0 tank: its whole hull aims) keeps its body when
        //                mixed, fixed main gun and all, while only the donor weapon fires - a
        //                visible gun that never shoots. A MELEE body is fine: the harvester keeps
        //                its torso and tool arm and gains a shoulder cannon.
        // One probe instantiate per entity per session answers both, cached, at menu time.
        struct SwapAbility { public bool donate, receive; }
        static readonly Dictionary<string, SwapAbility> swap_ability_cache = new Dictionary<string, SwapAbility>();
        static SwapAbility SwapAbilityOf(string entity_id){
            if (swap_ability_cache.TryGetValue(entity_id, out var cached)) return cached;
            var ability = new SwapAbility();
            GameObject probe = null;
            try{
                var prefab = Resources.Load(EntityBalancingStore.PrefabLocation(entity_id));
                if (prefab != null){
                    probe = (GameObject)GameObject.Instantiate(prefab, new Vector3(0f, -10000f, 0f), Quaternion.identity);
                    var controller = probe.GetComponent<EntityController>();
                    Transform pivot = (controller == null || controller.aiming == null) ? null : GetPivotFromAiming(controller.aiming);
                    if (pivot != null){
                        bool structural = PivotIsStructural(probe.transform, pivot, null, entity_id);
                        bool is_building = false;
                        try { is_building = EntityBalancingStore.HasRole(entity_id, UnitRole.Building); } catch { }
                        ability.donate = is_building || !structural;
                        ability.receive = !structural || controller.melee;

                        // The swap transplants the ATTACK events (OnReadyToShoot, OnHasShot, ...). Two kinds of
                        // "weapon" do not live there and break in both directions:
                        //  - no attack cooldown: suicide bombs (PCXBigBomber, PCXBomber, RoboBomb, PCXTermiteHover).
                        //    Their attack is blowing themselves up; as a donor that made "Support Tank + PCX Big
                        //    Bomber", as a host it would never go off.
                        //  - map-range guns (range 50+: UltraTurret 600, SupportSwarmArtillery 500,
                        //    PCXMissileArtillery 250): aimed and fired through their SKILL and its fire zone. As a
                        //    donor the gun never fires; as a host the skill spends its MP on a weapon that is gone.
                        float range = EntityBalancingStore.WeaponRange(entity_id, returnOriginalValueFromBalancingFile: true);
                        float cooldown = EntityBalancingStore.Attack1Cooldown(entity_id, returnOriginalValueFromBalancingFile: true);
                        bool real_gun = cooldown > 0.01f && range > 0.01f && range < 50f;
                        ability.donate &= real_gun;
                        ability.receive &= cooldown > 0.01f && range < 50f; // melee hosts have range 0 and are fine

                        // A host whose SKILL fires projectiles resolves their hits in its own OnAttackHitTarget
                        // (keyed on the projectile index) - one of the events the swap deletes and replaces with the
                        // donor's. MissileArtillery <- LaserCannonTurret: the skill missiles flew, hit, and did 0
                        // damage with no splash. Such hosts keep their own weapon.
                        if (SkillFiresProjectiles(controller)) ability.receive = false;
                    }
                }
            } catch (Exception e){ RCMManager.Log("swap probe failed for " + entity_id + ": " + e.Message); }
            // Immediate: Destroy waits for the end of the frame, and a probe made early in a frame got its
            // components' Update first - ScaleByChangeableValue then read stats off an entity that was never
            // initialised (KeyNotFoundException 'WeaponRange', one per donor, now and then at the menu).
            finally { if (probe != null) GameObject.DestroyImmediate(probe); }
            swap_ability_cache[entity_id] = ability;
            return ability;
        }
        // what value drives a ScaleByChangeableValue box on a given unit
        static float StatBehind(ScaleByChangeableValue scaleable, EntityController controller){
            if (controller == null) return 0f;
            try {
                return scaleable.scaleBy == ScaleByChangeableValue.ScaleBy.EffectRadius2
                    ? controller.EffectRadius2
                    : controller.GetChangeableValueAsFloat(scaleable.changeableValue);
            } catch { return 0f; }
        }

        // how long a unit spawned by a transplanted weapon lives (0 = leave as authored)
        public static float SpawnedUnitLifetime = 10f;

        static bool IsUnit(string entity_id){
            try { return EntityBalancingStore.HasRole(entity_id, UnitRole.Unit) && !EntityBalancingStore.HasRole(entity_id, UnitRole.Building); }
            catch { return false; }
        }

        static bool IsFiringEvent(EntityController.Event e)
            => e == EntityController.Event.OnAttackHitTarget || e == EntityController.Event.OnAttackMissedTarget
            || e == EntityController.Event.OnAttackWarmUpStarted || e == EntityController.Event.OnHasShot
            || e == EntityController.Event.OnReadyToShoot;

        static bool SkillFiresProjectiles(EntityController controller){
            foreach (var _event in controller.events){
                if (_event.@event != EntityController.Event.OnActivateSkill) continue;
                if (ContainsShot(_event.actions)) return true;
                foreach (var conditional in _event.conditionalActions)
                    if (ContainsShot(conditional.actions)) return true;
            }
            return false;
        }
        static bool ContainsShot(List<IEntityAction> actions){
            if (actions == null) return false;
            foreach (var action in actions){
                if (action is ShootProjectile) return true;
                if (action is RunSerial serial && ContainsShot(serial.actions)) return true;
            }
            return false;
        }
        public static bool CanDonate(string entity_id) => SwapAbilityOf(entity_id).donate;
        public static bool CanReceive(string entity_id) => SwapAbilityOf(entity_id).receive;

        static System.Random rng = new System.Random();
        static string GetRandomSupportedEntity(){
            if (supported_entities.Count == 0) return "CareTank";
            return supported_entities.ElementAt(rng.Next(supported_entities.Count));
        }


        static void CloneAimingComponentsTo(EntityController __instance, List<SingleTargetAction> output_aiming_components, SingleTargetAction aiming){
            if (aiming.GetType() == typeof(SerialSingleTargetAction)){
                SerialSingleTargetAction serialAction = (SerialSingleTargetAction)aiming;
                for (int i = 0; i < serialAction.actions.Count; i++)
                    CloneAimingComponentsTo(__instance, output_aiming_components, serialAction.actions[i]);

            } else if (aiming.GetType() == typeof(RotateInSingleTargetDirectionAroundAxisAction)){
                RotateInSingleTargetDirectionAroundAxisAction curr = (RotateInSingleTargetDirectionAroundAxisAction)aiming;
                RotateInSingleTargetDirectionAroundAxisAction new_action = __instance.gameObject.AddComponent<RotateInSingleTargetDirectionAroundAxisAction>();
                new_action.needsPreviousReadyToSetUp = curr.needsPreviousReadyToSetUp;
                new_action.needsNextIdleToSetUp = curr.needsNextIdleToSetUp;
                new_action.needsNextIdleToTearDown = curr.needsNextIdleToTearDown;
                new_action.transformToRotate = curr.transformToRotate;
                new_action.degreesPerSecond = curr.degreesPerSecond;
                new_action.direction = curr.direction;
                new_action.minDegrees = curr.minDegrees;
                new_action.maxDegrees = curr.maxDegrees;
                new_action.doNotRotateBackOnTeardown = curr.doNotRotateBackOnTeardown;
                output_aiming_components.Add(new_action);

            } else if (aiming.GetType() == typeof(RotateInSingleTargetDirectionAction)){
                RotateInSingleTargetDirectionAction curr = (RotateInSingleTargetDirectionAction)aiming;
                RotateInSingleTargetDirectionAction new_action = __instance.gameObject.AddComponent<RotateInSingleTargetDirectionAction>();
                new_action.needsPreviousReadyToSetUp = curr.needsPreviousReadyToSetUp;
                new_action.needsNextIdleToSetUp = curr.needsNextIdleToSetUp;
                new_action.needsNextIdleToTearDown = curr.needsNextIdleToTearDown;
                new_action.transformToRotate = curr.transformToRotate;
                new_action.degreesPerSecond = curr.degreesPerSecond;
                new_action.doNotRotateBackOnTeardown = curr.doNotRotateBackOnTeardown;
                output_aiming_components.Add(new_action);

            }else if (aiming.GetType() == typeof(RotateToBallisticAngleSingleTargetAction)){
                RotateToBallisticAngleSingleTargetAction curr = (RotateToBallisticAngleSingleTargetAction)aiming;
                RotateToBallisticAngleSingleTargetAction new_action = __instance.gameObject.AddComponent<RotateToBallisticAngleSingleTargetAction>();
                new_action.needsPreviousReadyToSetUp = curr.needsPreviousReadyToSetUp;
                new_action.needsNextIdleToSetUp = curr.needsNextIdleToSetUp;
                new_action.needsNextIdleToTearDown = curr.needsNextIdleToTearDown;
                new_action.transformToRotate = curr.transformToRotate;
                new_action.degreesPerSecond = curr.degreesPerSecond;
                output_aiming_components.Add(new_action);

            } else throw new InvalidOperationException("Unsupported aiming type ");
        }
        // Which transform a single (already flattened) aiming action actually drives.
        static Transform TransformRotatedBy(SingleTargetAction aiming){
            if (aiming is RotateInSingleTargetDirectionAroundAxisAction around) return around.transformToRotate;
            if (aiming is RotateInSingleTargetDirectionAction direction) return direction.transformToRotate;
            if (aiming is RotateToBallisticAngleSingleTargetAction ballistic) return ballistic.transformToRotate;
            return null;
        }

        static Transform GetPivotFromAiming(SingleTargetAction aiming){
            if (aiming.GetType() == typeof(SerialSingleTargetAction)) {
                SerialSingleTargetAction serialAction = (SerialSingleTargetAction)aiming;
                for (int i = 0; i < serialAction.actions.Count; i++){
                    Transform pivot = GetPivotFromAiming(serialAction.actions[i]);
                    if (pivot != null) return pivot;
            }} else if (aiming.GetType() == typeof(RotateInSingleTargetDirectionAroundAxisAction)){
                RotateInSingleTargetDirectionAroundAxisAction curr = (RotateInSingleTargetDirectionAroundAxisAction)aiming;
                if (curr.direction != RectTransform.Axis.Vertical) return curr.transformToRotate;
            }else if (aiming.GetType() == typeof(RotateInSingleTargetDirectionAction)){
                RotateInSingleTargetDirectionAction curr = (RotateInSingleTargetDirectionAction)aiming;
                return curr.transformToRotate;
            }
            return null;
        }
        
        // ---- Measurement -----------------------------------------------------------------------
        // EVERYTHING below measures in the local space of the UNIT ROOT, never in world space.
        // World-space AABBs made the card preview and the battlefield disagree for five rounds:
        // a card model sits in a tilted, scaled hierarchy, a spawned unit faces wherever it was
        // built, and an axis-aligned world box of the same mesh comes out different each time -
        // so the same unit was classified, scaled and clamped differently on the card than in
        // play. In root-local space the unit is always upright, unrotated and at its own scale,
        // and the preview path can simply run the SAME functions as the world path.

        // However snug the gun sits against the part it replaces, it must also fit the BODY it
        // lands on: a turret bigger than its chassis reads as the chassis being an accessory of
        // the gun (the harvester with a lance several times its own size).
        const float ChassisCapRatio = 1.15f;

        // Helper geometry every unit prefab carries and CreateEntityMesh strips from display
        // models - but only at end of frame, so it is still there when the preview measures.
        // Excluded by name so both paths see the same body.
        static readonly HashSet<string> helper_children = new HashSet<string>{
            "UnitSpawnedEffect", "BarCanvases2024", "SelectionCircles", "MinimapShape" };

        static bool IsHelperGeometry(Transform t, Transform root){
            for (Transform n = t; n != null && n != root; n = n.parent)
                if (helper_children.Contains(n.name) || n.name.IndexOf("FogOfWar", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        static bool BoundsIn(Transform space, Renderer r, out Bounds result){
            result = default;
            Bounds local; Transform basis = r.transform;
            if (r is SkinnedMeshRenderer skinned){
                local = skinned.localBounds;
                if (skinned.rootBone != null) basis = skinned.rootBone;
            } else {
                var filter = r.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) return false;
                local = filter.sharedMesh.bounds;
            }
            Matrix4x4 to_space = space.worldToLocalMatrix * basis.localToWorldMatrix;
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++){
                Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                Vector3 p = to_space.MultiplyPoint3x4(corner);
                if (i == 0) result = new Bounds(p, Vector3.zero); else result.Encapsulate(p);
            }
            return true;
        }

        static List<Bounds> PartsIn(Transform space, Transform root, Transform exclude_a, Transform exclude_b){
            var parts = new List<Bounds>();
            foreach (var r in root.GetComponentsInChildren<Renderer>()){
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!r.enabled) continue;
                if (exclude_a != null && IsChildOf(r.transform, exclude_a)) continue;
                if (exclude_b != null && IsChildOf(r.transform, exclude_b)) continue;
                if (IsHelperGeometry(r.transform, root)) continue;
                if (BoundsIn(space, r, out Bounds b)) parts.Add(b);
            }
            if (parts.Count < 2) return parts;
            // beam/effect meshes are stretched towards their target and report enormous bounds
            // (one turret measured 215657 units): drop anything far bigger than the typical part
            var sizes = parts.Select(b => Mathf.Max(b.size.x, b.size.y, b.size.z)).OrderBy(v => v).ToList();
            float limit = Mathf.Max(0.001f, sizes[sizes.Count / 2] * 4f);
            var kept = parts.Where(b => Mathf.Max(b.size.x, b.size.y, b.size.z) <= limit).ToList();
            return kept.Count > 0 ? kept : parts;
        }

        static bool Combine(List<Bounds> parts, out Bounds total){
            total = default;
            if (parts.Count == 0) return false;
            total = parts[0];
            for (int i = 1; i < parts.Count; i++) total.Encapsulate(parts[i]);
            return true;
        }

        static bool TryGetMeshBounds(Transform space, Transform root, out Bounds total, Transform exclude_a = null, Transform exclude_b = null){
            return Combine(PartsIn(space, root, exclude_a, exclude_b), out total);
        }

        // The single largest mesh part by volume. Combined boxes lie about where a body IS: an
        // antenna extends the top and drags the centre sideways. The biggest block is the torso or
        // hull on every unit that matters, and thin tall parts cannot skew it.
        static bool TryGetDominantBounds(Transform space, Transform root, out Bounds best, Transform exclude_a = null, Transform exclude_b = null){
            best = default;
            float best_volume = -1f;
            foreach (var b in PartsIn(space, root, exclude_a, exclude_b)){
                float volume = b.size.x * b.size.y * b.size.z;
                if (volume > best_volume){ best_volume = volume; best = b; }
            }
            return best_volume > 0f;
        }

        static float TotalVolume(List<Bounds> parts){
            float sum = 0f;
            foreach (var b in parts) sum += b.size.x * b.size.y * b.size.z;
            return sum;
        }

        // Size of a turret for scale matching. Thin parts (antennas, whip aerials) are ignored as
        // long as something solid remains: the support tank's aerial made its gun measure 7.9 wide
        // on a 5-wide hull, and every donor was then grown x2.5 to "match" it.
        static float Footprint(Transform space, Transform root){
            var parts = PartsIn(space, root, null, null);
            var solid = parts.Where(b => {
                float max = Mathf.Max(b.size.x, b.size.y, b.size.z);
                float min = Mathf.Min(b.size.x, b.size.y, b.size.z);
                return max > 0.0001f && min / max >= 0.06f;
            }).ToList();
            if (!Combine(solid.Count > 0 ? solid : parts, out Bounds total)) return 0f;
            // height counts too: a tower of a turret on a flat chassis looks as wrong as a wide one
            return Mathf.Max(total.size.x, total.size.z, total.size.y * 0.8f);
        }

        // The one rule for how big a transplanted turret gets. Aims SMALLER than the old turret (a
        // snug gun reads better than a bulky one), leaves a good-enough fit alone, and clamps
        // asymmetrically: growing is capped hard because a grown gun dominates the silhouette,
        // shrinking barely at all - long lance donors legitimately need x0.15 to sit on a small bot.
        public static float TurretScaleFactor(float old_size, float new_size, float target = 0.85f){
            if (old_size < 0.001f || new_size < 0.001f) return 1f;
            float factor = old_size / new_size * target;
            if (factor > 0.9f && factor < 1.1f) return 1f; // fits well enough already
            return Mathf.Clamp(factor, 0.12f, 2.5f);
        }

        // Harvester-style bots aim with their whole upper body: the pivot the aiming drives IS the
        // torso, so hiding it beheads the model. The decisive question is what REMAINS when the
        // pivot is hidden - a real turret leaves the hull it stood on, a torso leaves a pair of
        // feet - judged by SUMMED part volume. Footprint fails here (feet are tiny but stand wide)
        // and so does any single "dominant" part (a torso built from many small meshes has no big
        // block, which is how the harvester briefly lost its torso again). The measurements are
        // logged once per unit, so thresholds get tuned from data instead of from screenshots.
        // Set from measurements, not taste. The first logged run put the harvester bot at 0.61 -
        // one hundredth over the 0.60 this started at, so its torso was hidden - with walkers
        // around 0.4-0.7 and real turret carriers from 1.4 (PCX CF Tank) to 2.4 (Support Tank).
        const float StructuralVolumeRatio = 0.75f;

        // (Why a torso cannot donate: the chassis cap barely shrinks it, and the walk cycle the swap
        // carries over drags it back to walker height. Why buildings are exempt: a gun
        // emplacement's head is nearly the whole building, so it MEASURES like a torso - 55 of 202
        // donors were being refused that way - yet it is the most mountable thing in the game.
        // Both rules live in SwapAbilityOf.)

        static readonly HashSet<string> structural_logged = new HashSet<string>();
        static bool PivotIsStructural(Transform unit_root, Transform pivot, Transform donor_pivot, string label = null){
            var pivot_parts = PartsIn(unit_root, pivot, null, null);
            var rest_parts = PartsIn(unit_root, unit_root, pivot, donor_pivot);
            if (!Combine(pivot_parts, out Bounds pivot_b)) return false;
            float pivot_size = Mathf.Max(pivot_b.size.x, pivot_b.size.z);
            float unit_size = pivot_size;
            if (Combine(rest_parts, out Bounds rest_b)){
                rest_b.Encapsulate(pivot_b);
                unit_size = Mathf.Max(rest_b.size.x, rest_b.size.z);
            }
            float footprint_ratio = unit_size > 0.001f ? pivot_size / unit_size : 0f;
            float pivot_volume = TotalVolume(pivot_parts);
            float volume_ratio = pivot_volume > 0.0001f ? TotalVolume(rest_parts) / pivot_volume : float.MaxValue;
            bool structural = footprint_ratio > 0.55f && volume_ratio < StructuralVolumeRatio;
            if (VerboseLog && label != null && structural_logged.Add(label))
                RCMManager.Log($"structural check {label}: pivot/unit footprint {footprint_ratio:F2}, rest/pivot volume {volume_ratio:F2} -> {(structural ? "TORSO" : "turret")}");
            return structural;
        }

        // A donor is instantiated at its own prefab's scale and then measured in the local space of the
        // HOST ROOT - so the factor that comes out depends on how big that root is, and the two paths
        // do not have the same one. The card's display model is the prefab scaled to fit a card
        // (CreateEntityMesh multiplies the prefab's own scale by the card's), while a spawned unit's
        // root is whatever it was instantiated at, which a Titan or any other resize moves away from
        // the prefab. Whatever that difference is, it divides into every pair identically - which is
        // why two unrelated pairs, RoboCrystalHarvester <- Incinerator and BountyTank <- JeepWith-
        // MachineGun, both came out x1.186 apart in the same battle.
        //
        // Both paths now bring the donor into the HOST PREFAB's units before anything is measured, so
        // the factor means the same thing on the card and in the world. The card path already did this
        // - that is what its compensation line was for - and the world path did not.
        static readonly Dictionary<string, float> prefab_scale_cache = new Dictionary<string, float>();
        static float HostPrefabScale(string base_entity_id){
            if (prefab_scale_cache.TryGetValue(base_entity_id, out float cached)) return cached;
            float scale = 1f;
            try{
                var prefab = Resources.Load(EntityBalancingStore.PrefabLocation(base_entity_id)) as GameObject;
                if (prefab != null) scale = Mathf.Max(0.0001f, prefab.transform.lossyScale.x);
            } catch { }
            prefab_scale_cache[base_entity_id] = scale;
            return scale;
        }

        static void NormaliseDonorToHost(Transform donor_pivot, Transform host_root, string base_entity_id){
            if (donor_pivot == null || host_root == null) return;
            float k = Mathf.Max(0.0001f, host_root.lossyScale.x) / HostPrefabScale(base_entity_id);
            if (Mathf.Abs(k - 1f) > 0.001f) donor_pivot.localScale *= k;
        }

        // The card and the battlefield run the SAME seating code, so a visible difference between the
        // two is a bug, not a matter of taste - reported for MachineGun Turret + Repeater Turret. What
        // is compared is what a player can actually SEE: how big the seated turret ends up and where it
        // sits, both in root-local units (the unit's own frame, upright and at its own scale) and both
        // measured after scaling AND alignment. Comparing the raw scale factor instead, as this did
        // before, compares two numbers that are not in the same units and flags pairs that seat alike.
        struct Seating { public float Size; public Vector3 Centre; }
        static readonly Dictionary<string, Seating> preview_seating = new Dictionary<string, Seating>();
        static readonly Dictionary<string, Seating> world_seating = new Dictionary<string, Seating>();
        static readonly HashSet<string> seating_compared = new HashSet<string>();
        static void RecordSeating(string pair, Transform unit_root, Transform turret, bool preview){
            if (pair == null || unit_root == null || turret == null) return;
            var seating = new Seating { Size = Footprint(unit_root, turret) };
            if (TryGetMeshBounds(unit_root, turret, out Bounds b)) seating.Centre = b.center;
            (preview ? preview_seating : world_seating)[pair] = seating;
            if (!preview_seating.ContainsKey(pair) || !world_seating.ContainsKey(pair) || !seating_compared.Add(pair)) return;
            Seating p = preview_seating[pair], w = world_seating[pair];
            float scale = Mathf.Max(p.Size, w.Size);
            if (scale < 0.0001f) return;
            float size_ratio = (p.Size > 0.0001f && w.Size > 0.0001f) ? Mathf.Max(p.Size / w.Size, w.Size / p.Size) : 1f;
            float shift = Vector3.Distance(p.Centre, w.Centre) / scale;
            if (size_ratio > 1.1f || shift > 0.25f)
                RCMManager.Log("card and battlefield seat the turret differently for " + pair
                    + ": card size " + p.Size.ToString("0.##") + " at " + p.Centre.ToString("0.##")
                    + ", unit size " + w.Size.ToString("0.##") + " at " + w.Centre.ToString("0.##")
                    + " (size x" + size_ratio.ToString("0.##") + ", centre off by " + (shift * 100f).ToString("0") + "% of the turret)");
        }

        static void MatchTurretScale(Transform old_turret, Transform new_turret, Transform unit_root, bool structural){
            float old_size = Footprint(unit_root, old_turret);
            float new_size = Footprint(unit_root, new_turret);
            // a gun RIDING the torso should stay clearly smaller than it; one REPLACING a turret
            // matches it snugly
            float factor = TurretScaleFactor(old_size, new_size, structural ? 0.6f : 0.85f);
            if (TryGetMeshBounds(unit_root, unit_root, out Bounds chassis_b, old_turret, new_turret)){
                float chassis = Mathf.Max(chassis_b.size.x, chassis_b.size.z);
                if (chassis > 0.001f && new_size > 0.001f)
                    factor = Mathf.Min(factor, Mathf.Max(0.05f, ChassisCapRatio * chassis / new_size));
            }
            if (Mathf.Abs(factor - 1f) < 0.0001f) return;
            new_turret.localScale *= factor;
            if (log_details) RCMManager.Log($"scaled transplanted turret x{factor:F2} (old footprint {old_size:F1}, new {new_size:F1}{(structural ? ", torso mount" : "")})");
        }

        // The swap puts the donor PIVOT where the old pivot was, but a donor's mesh can sit far
        // from its own pivot, leaving the gun beside the body. Move the pivot until the new
        // turret's mesh sits where it belongs: over the old part's dominant block, resting at its
        // base - or, when the old part is a kept torso, sunk a third into its top.
        static void AlignTransplantedTurret(Transform unit_root, Transform old_turret, Transform new_turret, bool sit_on_top = false){
            if (!TryGetMeshBounds(unit_root, new_turret, out Bounds new_b)) return;
            if (!TryGetDominantBounds(unit_root, old_turret, out Bounds anchor)
                && !TryGetMeshBounds(unit_root, old_turret, out anchor)) return;

            float target_y = sit_on_top
                ? anchor.max.y + new_b.extents.y - Mathf.Min(new_b.size.y, anchor.size.y) * 0.35f
                : anchor.min.y + new_b.extents.y;
            Vector3 offset = new Vector3(anchor.center.x, target_y, anchor.center.z) - new_b.center;
            if (offset.sqrMagnitude > 0.0001f) new_turret.position += unit_root.TransformVector(offset);

            // Contact clamp: whatever the pivot claimed (a small emitter halfway up a mast), a
            // replaced turret must touch the body. Not for torso mounts, whose anchor is the torso.
            //
            // It used to compare against the top of EVERYTHING that is left, which only catches a gun
            // floating above the whole unit. A gun parked beside a mast is above the hull it should be
            // resting on while still below the mast's tip, so nothing pulled it down - and the mast is
            // often part of the old turret and gets its renderers disabled straight after, leaving the
            // gun hanging over a gap. That is the "weapon floating above my support unit" report: the
            // Robo Medic's own pivot measures 1.0 across, an emitter rather than a turret, and the gun
            // was seated at ITS height.
            //
            // So the support is looked for UNDER the turret: the highest body part whose footprint
            // actually overlaps the turret's, which is the surface it would rest on. Only if nothing
            // sits beneath it at all does this fall back to the top of the body, the old behaviour.
            if (!sit_on_top
                && TryGetMeshBounds(unit_root, unit_root, out Bounds body, old_turret, new_turret)
                && TryGetMeshBounds(unit_root, new_turret, out Bounds seated)){
                float support = body.max.y;
                bool local = false;
                foreach (var part in PartsIn(unit_root, unit_root, old_turret, new_turret)){
                    if (part.max.x < seated.min.x || part.min.x > seated.max.x) continue;
                    if (part.max.z < seated.min.z || part.min.z > seated.max.z) continue;
                    if (!local || part.max.y > support) support = part.max.y;
                    local = true;
                }
                // a gap worth closing, not the wobble of a gun that already sits on its mount
                float drop = seated.min.y - (support - 0.15f * seated.size.y);
                if (drop > 0.05f * Mathf.Max(0.001f, seated.size.y)){
                    new_turret.position += unit_root.TransformVector(Vector3.down * drop);
                    if (log_details) RCMManager.Log($"contact clamp pulled turret down by {drop:F2} onto the {(local ? "body under it" : "top of the body")}");
                }
            }
            if (log_details) RCMManager.Log($"aligned transplanted turret by {offset.magnitude:F2}{(sit_on_top ? " (onto torso)" : "")}");
        }

        // Card models and building placement previews come from EntityFactory.CreateEntityMesh.
        // When an external DonorSelector is set (stable per-run donors), transplant the donor
        // turret onto those display models too, so the blueprint card shows the actual unit.
        // The EntityController on the display model is Destroy()ed by CreateEntityMesh but that
        // is deferred, so it is still readable this frame.
        [HarmonyPatch(typeof(EntityFactory), "CreateEntityMesh")]
        public static class Patch_EntityFactory_CreateEntityMesh{
            [HarmonyPostfix]
            public static void Postfix(string entityId, GameObject __result){
                try{
                    if (__result == null || DonorSelector == null) return;
                    if (!supported_entities.Contains(entityId) || !CanReceive(entityId)) return;
                    string donor_id = DonorSelector(entityId);
                    if (string.IsNullOrEmpty(donor_id) || !supported_entities.Contains(donor_id)) return;
                    var timer = StartTiming();
                    ApplyVisualSwap(__result, entityId, donor_id);
                    ReportTiming(timer, "preview swap", entityId + " <- " + donor_id);
                } catch (Exception e){ RCMManager.Log("preview turret swap failed: " + e.Message); }
            }
        }

        // timing helper shared by the swap paths
        // Phase marks inside one swap: a spawn that costs 20ms is a dropped frame, and the total
        // alone cannot say whether it is the donor instantiate, the bounds measuring or the event
        // surgery. Cheap (a Stopwatch read per mark), reported only with a slow swap.
        static readonly List<KeyValuePair<string, double>> swap_phases = new List<KeyValuePair<string, double>>();
        static double swap_last_mark;
        static void Mark(System.Diagnostics.Stopwatch watch, string phase){
            if (watch == null) return;
            double now = watch.Elapsed.TotalMilliseconds;
            swap_phases.Add(new KeyValuePair<string, double>(phase, now - swap_last_mark));
            swap_last_mark = now;
        }
        static string Phases(){
            if (swap_phases.Count == 0) return "";
            var parts = new List<string>();
            foreach (var p in swap_phases) parts.Add($"{p.Key} {p.Value:F1}");
            return " [" + string.Join(", ", parts) + "]";
        }
        static System.Diagnostics.Stopwatch StartTiming(){
            swap_phases.Clear(); swap_last_mark = 0;
            return LogSwapsSlowerThanMs > 0 ? System.Diagnostics.Stopwatch.StartNew() : null;
        }
        // Swap details are logged the FIRST time a base/donor pair is seen, not per spawn. A third
        // of a session's log was the same four lines repeated for every harvester that walked out
        // of the HQ; the first occurrence carries all the information the rest repeat.
        static readonly HashSet<string> logged_pairs = new HashSet<string>();
        static bool log_details = true;

        static void ReportTiming(System.Diagnostics.Stopwatch watch, string what, string detail){
            if (watch == null) return;
            watch.Stop();
            double ms = watch.Elapsed.TotalMilliseconds;
            // a known pair is only worth a line again when it is genuinely slow
            if (ms >= LogSwapsSlowerThanMs && (log_details || ms >= LogSwapsSlowerThanMs * 5))
                RCMManager.Log($"MixNMatch PERF: {what} took {ms:F1}ms ({detail})" + (ms >= LogSwapsSlowerThanMs * 5 ? Phases() : ""));
        }
        static void ApplyVisualSwap(GameObject display_model, string base_entity_id, string donor_id){
            log_details = logged_pairs.Add(base_entity_id + "<-" + donor_id + " (card)");
            EntityController display_controller = display_model.GetComponent<EntityController>();
            // Deliberately NOT gated on skillAiming: the in-world swap now nulls it and mixes those
            // units, so refusing them here would put a stock model on the card for a unit that
            // spawns transplanted. Nothing on a display model aims anyway - it is stripped to
            // meshes below - so the field has no meaning in this path.
            if (display_controller == null || display_controller.aiming == null) return;
            Transform old_pivot = GetPivotFromAiming(display_controller.aiming);
            if (old_pivot == null) return;

            GameObject donor_obj = (GameObject)GameObject.Instantiate(Resources.Load(EntityBalancingStore.PrefabLocation(donor_id)), new Vector3(0, 0, 0), Quaternion.identity);
            try{
                EntityController donor_controller = donor_obj.GetComponent<EntityController>();
                Transform new_pivot = (donor_controller == null || donor_controller.aiming == null) ? null : GetPivotFromAiming(donor_controller.aiming);
                if (new_pivot == null) return;
                // mirror of the world path: torso donors are refused there, so the card must show
                // the stock unit too
                if (!CanDonate(donor_id)) return;

                new_pivot.SetParent(old_pivot.parent);
                new_pivot.position = old_pivot.position;
                new_pivot.rotation = old_pivot.rotation;
                // The donor was instantiated at WORLD scale while this model lives in a card-scaled
                // hierarchy. Bring it into the card's scale first - the same ratio the base unit
                // itself was shrunk by - and from here on the preview is just the world swap: same
                // classification, same clamped scale factor, same alignment, all in root-local space.
                NormaliseDonorToHost(new_pivot, display_model.transform, base_entity_id);
                // display model only: strip everything but the meshes so nothing ticks or reacts
                foreach (var comp in new_pivot.GetComponentsInChildren<Component>(true)){
                    if (comp is Transform || comp is MeshFilter || comp is MeshRenderer || comp is SkinnedMeshRenderer) continue;
                    GameObject.Destroy(comp);
                }
                bool structural = PivotIsStructural(display_model.transform, old_pivot, new_pivot, base_entity_id + " (card)");
                if (ScaleTransplantedTurrets)
                    MatchTurretScale(old_pivot, new_pivot, display_model.transform, structural);
                AlignTransplantedTurret(display_model.transform, old_pivot, new_pivot, sit_on_top: structural);
                RecordSeating(base_entity_id + " <- " + donor_id, display_model.transform, new_pivot, preview: true);
                // match the display layer or the card/preview camera won't render it
                int display_layer = old_pivot.gameObject.layer;
                foreach (var t in new_pivot.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = display_layer;

                // a structural pivot (torso) stays visible; hiding it beheads the card model
                if (!structural){
                    foreach (var r in old_pivot.GetComponentsInChildren<Renderer>()) r.enabled = false;
                    foreach (var p in old_pivot.GetComponentsInChildren<ParticleSystem>()) p.gameObject.SetActive(false);
                }
            } finally{
                GameObject.Destroy(donor_obj);
            }
        }

        public static bool IsChildOf(Transform child, Transform potentialParent){
            if (child == potentialParent) return true;
            Transform t = child;
            while (t != null){
                if (t == potentialParent) return true;
                t = t.parent;
            }
            return false;
        }

        public static AudioSource CopyAudioSource(AudioSource from, GameObject to){
            AudioSource a = to.AddComponent<AudioSource>();
            a.clip = from.clip;
            a.outputAudioMixerGroup = from.outputAudioMixerGroup;
            a.volume = from.volume;
            a.pitch = from.pitch;
            a.panStereo = from.panStereo;
            a.spatialBlend = from.spatialBlend;
            a.reverbZoneMix = from.reverbZoneMix;
            a.loop = from.loop;
            a.mute = from.mute;
            a.playOnAwake = from.playOnAwake;
            a.bypassEffects = from.bypassEffects;
            a.bypassListenerEffects = from.bypassListenerEffects;
            a.bypassReverbZones = from.bypassReverbZones;
            a.priority = from.priority;
            a.dopplerLevel = from.dopplerLevel;
            a.spread = from.spread;
            a.minDistance = from.minDistance;
            a.maxDistance = from.maxDistance;
            return a;
        }



        // this hook is called when a unit is created & manually initialized via the game, usually happening a few lines after the instantiation
        [HarmonyPatch(typeof(EntityController), "Init")]
        public static class Patch_EntityController_Init{
            [HarmonyPrefix]
            public static bool Prefix(EntityController __instance, EntityController originEntity){
                if (__instance.aiming == null
                || !supported_entities.Contains(__instance.entityId)
                || !CanReceive(__instance.entityId)) return true; // see SwapAbilityOf: no dead guns

                var swap_timer = StartTiming();

                // just outright remove skill aiming so we dont get any weird stuff
                __instance.skillAiming = null;

                // get current turret object from current unit
                Transform current_turret_pivot = GetPivotFromAiming(__instance.aiming);
                if (current_turret_pivot == null) return true; // this shouldn't be possible but as a failsafe...

                // snapshot the current aiming components but do NOT destroy them yet: if the donor
                // turns out to use an unsupported aiming type the clone below throws, and a unit
                // whose aiming was already destroyed never turns towards enemies again (planter
                // turret bug). they get destroyed only after the clone succeeded
                var old_aiming_comps = __instance.gameObject.GetComponents<SingleTargetAction>();

                // grab another unit to frankenstien onto
                // an external DonorSelector (e.g. a seeded randomizer) takes priority, otherwise
                // fall back to the built-in per-spawn random pick
                string frankenstien_id = DonorSelector?.Invoke(__instance.entityId);
                if (frankenstien_id == "") return true; // selector opted this entity out of mixing
                if (frankenstien_id == null || !supported_entities.Contains(frankenstien_id))
                    frankenstien_id = GetRandomSupportedEntity();
                log_details = logged_pairs.Add(__instance.entityId + "<-" + frankenstien_id);
                if (log_details) RCMManager.Log("mixing units, base entityID: " + __instance.entityId + ", turret from: " + frankenstien_id);

                GameObject frankenstien_entity_obj = (GameObject)GameObject.Instantiate(Resources.Load(EntityBalancingStore.PrefabLocation(frankenstien_id)), new Vector3(0, 0, 0), Quaternion.identity);
                EntityController frankenstien_controller = frankenstien_entity_obj.GetComponent<EntityController>();
                Mark(swap_timer, "instantiate donor");

                // whether a unit charges in or shoots from afar belongs to the weapon, not the chassis:
                // a transplanted melee weapon (poker) on a ranged chassis would otherwise be swung
                // from across the map. Init builds EntityAttack from these fields right after this
                // prefix, so setting them here is enough. the extension is padded a little because
                // the new chassis reaches from its own collision radius
                if (__instance.melee != frankenstien_controller.melee)
                    if (log_details) RCMManager.Log("weapon is " + (frankenstien_controller.melee ? "melee" : "ranged") + ", switching " + __instance.entityId + " to match");
                __instance.melee = frankenstien_controller.melee;
                __instance.meleeRadiusExtension = frankenstien_controller.melee
                    ? frankenstien_controller.meleeRadiusExtension + 0.5f
                    : frankenstien_controller.meleeRadiusExtension;

                // NOTE: for now we have to delete all of these components on the new turret because they have references that escape the turret gameobject
                // we could fix these up to reference the `__instance` variable instead, but haven't tested if this works or not
                ScaleByChangeableValue[] scaleables = frankenstien_entity_obj.GetComponentsInChildren<ScaleByChangeableValue>();
                // these boxes size themselves in LOCAL scale from a stat (weapon range etc) but are hit-tested by their WORLD
                // size, so remember the scale they were authored under: shrinking the turret to fit the new chassis would
                // otherwise shrink the weapon's real reach with it while the unit still opens fire at its full weapon range
                var authored_scales = new Dictionary<ScaleByChangeableValue, float>();
                foreach (var scaleable in scaleables)
                    if (scaleable.transform.parent != null) authored_scales[scaleable] = scaleable.transform.parent.lossyScale.z;
                // A box driven by a stat the HOST does not have collapses to nothing, and every weapon that
                // damages "whatever is inside this box" then deals nothing at all. Robo Poker's damage is
                // DealDamage(Damage1 via Identified:RoboPokeScalableAttackWR) and that box is sized from
                // weapon range - on a melee Claw Bot (range 0) it became a box of zero size, so the claw
                // swung, animated and hurt nobody. When the host's stat is zero the box keeps the size the
                // donor authored instead of following the host.
                foreach (var scaleable in scaleables){
                    float donor_value = StatBehind(scaleable, frankenstien_controller);
                    float host_value = StatBehind(scaleable, __instance);
                    scaleable.entityController = __instance;
                    if (host_value > 0.001f || donor_value <= 0.001f) continue;
                    float size = donor_value * scaleable.multiplier;
                    Vector3 frozen = Vector3.one;
                    if ((scaleable.axis & ScaleByChangeableValue.Axis.X) != ScaleByChangeableValue.Axis.None) frozen.x = size;
                    if ((scaleable.axis & ScaleByChangeableValue.Axis.Y) != ScaleByChangeableValue.Axis.None) frozen.y = size;
                    if ((scaleable.axis & ScaleByChangeableValue.Axis.Z) != ScaleByChangeableValue.Axis.None) frozen.z = size;
                    scaleable.transform.localScale = frozen;
                    scaleable.enabled = false;
                    RCMManager.Log("hit box '" + scaleable.name + "' would collapse on " + __instance.entityId + " <- " + frankenstien_id
                        + " (its " + scaleable.scaleBy + " is 0 here): frozen at the donor's size " + size.ToString("0.##"));
                }

                // copy all of the aiming components from the new turret to current unit
                Transform frankenstien_pivot = GetPivotFromAiming(frankenstien_controller.aiming) ;
                List<SingleTargetAction> new_aiming_components = new List<SingleTargetAction>();
                try{
                    if (frankenstien_pivot == null) throw new InvalidOperationException("no usable pivot on donor");
                    // A donor whose pivot is its own torso (walkers aim with their whole upper
                    // body) is not a turret anyone can wear: the chassis cap barely shrinks it, and
                    // its walk/idle animations - which the swap carries over - reposition it to
                    // walker height every cycle, which is the giant mech hovering over the support
                    // tank. Such donors are refused, the unit stays stock.
                    if (!CanDonate(frankenstien_id))
                        throw new InvalidOperationException("donor's pivot is its torso, not a mountable turret");
                    CloneAimingComponentsTo(__instance, new_aiming_components, frankenstien_controller.aiming);
                    // Aiming components hold a DIRECT reference to the transform they rotate. Only
                    // the pivot subtree gets reparented onto us; anything the donor aimed outside it
                    // (a hull that turns to face, a second mount) dies with frankenstien_entity_obj
                    // below, and the cloned action then throws every frame it tries to aim. Refuse
                    // the donor instead - a stock unit beats a unit that screams into the log.
                    foreach (var cloned in new_aiming_components){
                        Transform rotates = TransformRotatedBy(cloned);
                        if (rotates == null)
                            throw new InvalidOperationException("cloned aiming has no transform to rotate");
                        if (!IsChildOf(rotates, frankenstien_pivot))
                            throw new InvalidOperationException("donor aims '" + rotates.name + "' from outside its turret pivot");
                    }
                } catch (Exception e){
                    // donor not swappable: clean up whatever was half-built and leave the unit stock
                    foreach (var added in new_aiming_components) GameObject.Destroy(added);
                    GameObject.Destroy(frankenstien_entity_obj);
                    if (log_details) RCMManager.Log("skipping swap for " + __instance.entityId + " <- " + frankenstien_id + ": " + e.Message);
                    return true;
                }
                // clone succeeded: NOW retire the old aiming components
                foreach (var comp in old_aiming_comps) GameObject.Destroy(comp);
                Mark(swap_timer, "aiming");
                if (new_aiming_components.Count == 0)
                    __instance.aiming = null;
                else if (new_aiming_components.Count == 1)  
                    __instance.aiming = new_aiming_components[0];
                else { // set serial & populate entries
                    SerialSingleTargetAction new_action = __instance.gameObject.AddComponent<SerialSingleTargetAction>();
                    new_action.actions = new_aiming_components;
                    __instance.aiming = new_action;
                }

                // here we cleanup animations to remove any extra references, and then add them to our new unit
                // NOTE: most of the animations that we give to the root unit will be overwritten anyway since we're blanket overwriting all shooting events
                // however this is just so extra potentially non-shooting animations can get through. IE skill activation could have the turret play its animation and such
                void FixupFrankenstienAnimations(EntityEvent _event, IEntityAction action){
                    if (action.GetType() == typeof(Animate)){
                        Animate animateAction = (Animate)action;
                        // basically remove this action from the frankenstien unit and give it to our actual unit
                        // so we look through all the animations to find ones that reference to the turret
                        int valid_tranforms = 0;
                        for (; valid_tranforms < animateAction.transforms.Count;){
                            if (!IsChildOf(animateAction.transforms[valid_tranforms], frankenstien_pivot)){
                                animateAction.transforms.RemoveAt(valid_tranforms);
                                // remove any instances that point to b, and decrease index of each with a greater value
                                for (int c = 0; c < animateAction.animationDescriptions.Count; c++){
                                    if (animateAction.animationDescriptions[c].transformIndex == valid_tranforms){
                                        animateAction.animationDescriptions.RemoveAt(c);
                                        c--;
                                    } else if (animateAction.animationDescriptions[c].transformIndex > valid_tranforms)
                                        animateAction.animationDescriptions[c].transformIndex -= 1;
                                }
                            } else valid_tranforms++;
                        }
                        // now if the animation has any transforms left, add it to new unit
                        if (valid_tranforms > 0){
                            // find a matching event
                            bool did_find = false;
                            foreach (var src_event in __instance.events){ 
                                if (src_event.@event == _event.@event){
                                    src_event.actions.Add(action); 
                                    did_find = true;
                                    break;
                            }}
                            // else create a new event to stick it under
                            if (!did_find){
                                EntityEvent new_eventy = new EntityEvent();
                                new_eventy.actions.Add(action);
                                new_eventy.@event = _event.@event;
                                __instance.events.Add(new_eventy);
                }}}}
                foreach (var _event in frankenstien_controller.events){
                    foreach (var conditional_action in _event.conditionalActions)
                        foreach (var action in conditional_action.actions)
                            FixupFrankenstienAnimations(_event, action);
                    foreach (var action in _event.actions)
                        FixupFrankenstienAnimations(_event, action);
                }


                // now we delete & replace our new unit's shooting related events
                for (int i = 0; i < __instance.events.Count; i++){
                    switch (__instance.events[i].@event){
                        case EntityController.Event.OnAttackHitTarget:
                        case EntityController.Event.OnAttackMissedTarget:
                        case EntityController.Event.OnAttackWarmUpStarted:
                        case EntityController.Event.OnHasShot:
                        case EntityController.Event.OnReadyToShoot:
                            __instance.events.RemoveAt(i);
                            i--;
                            break;
                        default: break;
                    }
                }

                // copy over entity identifiers
                // TODO: we might have to adjust the teams for the identifier??
                foreach (EntityIdentifier ident in frankenstien_controller.EntityIdentifiers){
                    bool already_exists = false;
                    foreach (EntityIdentifier our_ident in __instance.EntityIdentifiers)
                        if (ident.name == our_ident.name)
                            already_exists = true;
                    if (!already_exists){
                        __instance.EntityIdentifiers.Add(ident);
                        // an ident WITH a box gets the box migrated along; only one that selects by overlap box
                        // and has none left needs a fallback radius. this used to fire for every ident WITHOUT a
                        // box (null transform -> false), which overwrote authored radii like a grenade's
                        // SelfEffectRadius1 with the whole weapon range: splash and heal auras 10x too wide
                        if (ident.scaledOverlapBox != null)
                            IsChildOfOrCopyTopLevelChild(ident.scaledOverlapBox.gameObject.transform, true);
                        else if (ident.radius == EntityIdentifier.Radius.OverlapBox){
                            ident.radius = EntityIdentifier.Radius.SelfWeaponRange;
                            if (log_details) RCMManager.Log("entity ident '" + ident.name + "' selects by overlap box but has none, using weapon range: " + __instance.entityId + " <- " + frankenstien_id);
                        }
                    }
                }


                // helper func for if object is referenced by the an action but not apart of the turret, we check if its a top level child and migrate it to this object if it is
                bool IsChildOfOrCopyTopLevelChild(Transform t, bool force_copy_obj = false){
                    if (t == null) return false;
                    if (!IsChildOf(t, frankenstien_pivot)){
                        if (t.parent == frankenstien_entity_obj.transform || force_copy_obj)
                            t.SetParent(__instance.gameObject.transform);
                        // we then have to make sure this object hasn't already been migrated to our new unit
                        else return IsChildOf(t, __instance.gameObject.transform);
                    }
                    return true;
                }
                // now give new shooting events & fix them up where needed
                void FixupFiringActions(IEntityAction action){
                    // note: animate actions already updated above
                    if (action.GetType() == typeof(ShootProjectile)){
                        ShootProjectile typed_action = (ShootProjectile)action;
                        // either null out entity identifiers or copy them over from frankenstien unit, although not sure what this even does
                        // TODO: we will probably have to update each ones target indentifier to alter which team it targets, as if we copy an identifier from the PCX it likely wont beable to attack anything
                        //if (typed_action.chooseTargetFromEntityIdentifier){
                        //    RCMManager.Log("had to clear entity shooting targeting params off of unit \""+ typed_action.multipleTargetEntityIdentifier + "\"" + __instance.entityId + "->" + frankenstien_id + "");
                        //    typed_action.chooseTargetFromEntityIdentifier = false;
                        //    typed_action.multipleTargetEntityIdentifier = "";
                        //}
                        // not sure if this is needed but i suspect there would be problems otherwise
                        if (typed_action.shotSoundAudioSource != null)
                            typed_action.shotSoundAudioSource = CopyAudioSource(typed_action.shotSoundAudioSource, __instance.gameObject);
                    }
                    else if (action.GetType() == typeof(EnableDisable)){
                        EnableDisable typed_action = (EnableDisable)action;
                        if (!IsChildOfOrCopyTopLevelChild(typed_action.gameObject?.transform)) typed_action.gameObject = null;
                        if (!IsChildOfOrCopyTopLevelChild(typed_action.behaviour?.gameObject?.transform)) typed_action.behaviour = null;
                        if (!IsChildOfOrCopyTopLevelChild(typed_action.renderer?.gameObject?.transform)) typed_action.renderer = null;
                        if (!IsChildOfOrCopyTopLevelChild(typed_action.collider?.gameObject?.transform)) typed_action.collider = null;
                    }
                    else if (action.GetType() == typeof(SpawnObject)){
                        SpawnObject spawned = (SpawnObject)action;
                        // A weapon that spawns a UNIT is spawning a free army: the PCX Barrage Truck's shell
                        // drops a Nano Hunter on every impact, and on a player Artillery Truck that meant one
                        // permanent extra unit per shell. Spawned units from a transplanted weapon now live
                        // SpawnedUnitLifetime seconds and cost no unit slot, the way the game's own temporary
                        // summons work. Effects and prefabs are untouched.
                        if (spawned.spawn == SpawnObject.Spawn.EntityId && !string.IsNullOrEmpty(spawned.entityId)
                            && SpawnedUnitLifetime > 0f && spawned.timeToLiveMultiplier <= 0.01f && IsUnit(spawned.entityId)){
                            spawned.timeToLiveSource = EntityActionDuration.MultipleEntitySource.One;
                            spawned.timeToLiveMultiplier = SpawnedUnitLifetime;
                            spawned.ignoreUnitCapAlthoughNoSpawn = true;
                            RCMManager.Log("weapon of " + frankenstien_id + " spawns the unit " + spawned.entityId
                                + " on " + __instance.entityId + ": limited to " + SpawnedUnitLifetime.ToString("0") + "s");
                        }
                        SpawnObject typed_action = (SpawnObject)action;
                        //if (typed_action.operatingEntities == MultipleEntitiesActionWithoutUpdate.OperatingEntities.Identified){
                        //    RCMManager.Log("had to clear entity spawnobject targeting params off of unit \""+ typed_action.entityIdentifierWithTargetAsOrigin + "\"" + __instance.entityId + "->" + frankenstien_id + "");
                        //    typed_action.operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self;
                        //    typed_action.entityIdentifierWithTargetAsOrigin = "";
                        //}
                        if (typed_action.startingPosition == SpawnObject.StartingPosition.Transform){
                            if (!IsChildOfOrCopyTopLevelChild(typed_action.startingPositionTransform)){
                                typed_action.startingPosition = SpawnObject.StartingPosition.Self;
                                typed_action.startingPositionTransform = null;
                            }
                        }
                    }
                    else if (action.GetType() == typeof(DealDamage)){
                        DealDamage typed_action = (DealDamage)action;
                        //if (typed_action.operatingEntities == MultipleEntitiesActionWithoutUpdate.OperatingEntities.Identified){
                        //    RCMManager.Log("had to clear entity dealdamage targeting params off of unit \""+ typed_action.entityIdentifierWithTargetAsOrigin + "\"" + __instance.entityId + "->" + frankenstien_id + "");
                        //    typed_action.operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self;
                        //    typed_action.entityIdentifierWithTargetAsOrigin = "";
                        //}
                    }
                    else if (action.GetType() == typeof(DealDamageAdvanced)){
                        DealDamageAdvanced typed_action = (DealDamageAdvanced)action;
                        //if (typed_action.operatingEntities == MultipleEntitiesActionWithoutUpdate.OperatingEntities.Identified){
                        //    RCMManager.Log("had to clear entity dealdamageadvanced targeting params off of unit \""+ typed_action.entityIdentifierWithTargetAsOrigin + "\"" + __instance.entityId + "->" + frankenstien_id + "");
                        //    typed_action.operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self;
                        //    typed_action.entityIdentifierWithTargetAsOrigin = "";
                        //}
                    }
                    else if (action.GetType() == typeof(ConfigureLineRenderer)){
                        ConfigureLineRenderer typed_action = (ConfigureLineRenderer)action;
                        if (!IsChildOfOrCopyTopLevelChild(typed_action.lineRenderer?.gameObject?.transform, true)) typed_action.lineRenderer = null;
                    }
                    else if (action.GetType() == typeof(ConfigureLineRendererForUseWithEntityIdentifier)){
                        ConfigureLineRendererForUseWithEntityIdentifier typed_action = (ConfigureLineRendererForUseWithEntityIdentifier)action;
                        if (!IsChildOfOrCopyTopLevelChild(typed_action.lineRenderer?.gameObject?.transform, true)) typed_action.lineRenderer = null;
                        //if (typed_action.operatingEntities == MultipleEntitiesActionWithoutUpdate.OperatingEntities.Identified){
                        //    RCMManager.Log("had to clear entity confiurelinerender-entiityident targeting params off of unit \""+ typed_action.entityIdentifierWithTargetAsOrigin + "\"" + __instance.entityId + "->" + frankenstien_id + "");
                        //    typed_action.operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self;
                        //    typed_action.entityIdentifierWithTargetAsOrigin = "";
                        //}
                    }
                }
                foreach (var _event in frankenstien_controller.events){
                    switch (_event.@event){
                        case EntityController.Event.OnAttackHitTarget:
                        case EntityController.Event.OnAttackMissedTarget:
                        case EntityController.Event.OnAttackWarmUpStarted:
                        case EntityController.Event.OnHasShot:
                        case EntityController.Event.OnReadyToShoot:
                            __instance.events.Add(_event);
                            foreach (var conditional_action in _event.conditionalActions)
                                foreach (var action in conditional_action.actions)
                                    FixupFiringActions(action);
                            foreach (var action in _event.actions)
                                FixupFiringActions(action);
                            break;
                        default: break;
                    }
                }

                // A transplanted shot that picks its targets through a named identifier resolves that
                // name on the FIRING unit (ShootProjectile.Run -> EntitiesFromChosenIdentifier(name,
                // payload.Self, ...)). If the host has no identifier of that name, Run stops before a
                // projectile is ever spawned: the unit is told to fire, nothing leaves the barrel, and it
                // stands there looking idle with nothing in either log. The donor's identifiers are copied
                // above, so this should hold - but a name that did not make it through leaves exactly
                // that silent failure, so it is checked rather than trusted.
                foreach (var _event in __instance.events){
                    if (!IsFiringEvent(_event.@event)) continue;
                    foreach (var action in _event.actions) CheckShotIdentifier(action);
                    foreach (var conditional in _event.conditionalActions)
                        foreach (var action in conditional.actions) CheckShotIdentifier(action);
                }
                void CheckShotIdentifier(IEntityAction action){
                    var shot = action as ShootProjectile;
                    if (shot == null || !shot.chooseTargetFromEntityIdentifier || string.IsNullOrEmpty(shot.multipleTargetEntityIdentifier)) return;
                    foreach (var ident in __instance.EntityIdentifiers)
                        if (ident != null && ident.name == shot.multipleTargetEntityIdentifier) return;
                    string missing = shot.multipleTargetEntityIdentifier;
                    shot.chooseTargetFromEntityIdentifier = false; // fall back to the unit's current target
                    shot.multipleTargetEntityIdentifier = "";
                    RCMManager.Log("shot identifier '" + missing + "' missing on " + __instance.entityId
                        + " <- " + frankenstien_id + ": firing at the current target instead");
                }


                // this is technically redundant as we dont destroy any of the turret pieces for now
                // a pivot that is really the unit's body (walker torso, harvester) stays visible and alive, so its
                // animations must stay too: stripping them froze the body while the legs kept walking
                Mark(swap_timer, "events+animations");
                bool structural = PivotIsStructural(__instance.transform, current_turret_pivot, frankenstien_pivot, __instance.entityId + " (world)");
                Mark(swap_timer, "structural");
                bool CheckAndCleanAnimation(IEntityAction action){
                    if (action.GetType() == typeof(Animate)){
                        Animate fireProjectileAction = (Animate)action;
                        for (int b = 0; b < fireProjectileAction.transforms.Count; b++){
                            if (IsChildOf(fireProjectileAction.transforms[b], current_turret_pivot)){
                                fireProjectileAction.transforms.RemoveAt(b);
                                // then remove any animation descriptions for this one
                                for (int m = 0; m < fireProjectileAction.animationDescriptions.Count; m++){
                                    if (fireProjectileAction.animationDescriptions[m].transformIndex == b){
                                        fireProjectileAction.animationDescriptions.RemoveAt(m);
                                        m--;
                                    // everything behind the removed transform moved down one slot. without this the
                                    // remaining descriptions index past the end of `transforms` and Animate throws on
                                    // every play (walkers: turret parts listed before leg parts)
                                    } else if (fireProjectileAction.animationDescriptions[m].transformIndex > b){
                                        fireProjectileAction.animationDescriptions[m].transformIndex -= 1;
                                }}
                                b--;
                        }}
                        return (fireProjectileAction.transforms.Count == 0);
                        // we could also then remove the event if it has no actions left too, but that has yet to cause any issues
                    }
                    return false;
                }
                foreach (var _event in __instance.events){ if (structural) break;
                    foreach (var conditional_action in _event.conditionalActions){
                        for (int i = 0; i < conditional_action.actions.Count; i++){
                            var action = conditional_action.actions[i];
                            if (CheckAndCleanAnimation(action)){
                                conditional_action.actions.RemoveAt(i);
                                i--;
                            }
                        }
                    }
                    for (int i = 0; i < _event.actions.Count; i++){
                        var action = _event.actions[i];
                        // if true, it means all the transforms in this action had to be removed as they all referenced parts of the old turret that we want to remove
                        if (CheckAndCleanAnimation(action)){
                            _event.actions.RemoveAt(i);
                            i--;
                        }
                    }
                }

                // perform physical turret swap
                frankenstien_pivot.SetParent(current_turret_pivot.parent);
                frankenstien_pivot.position = current_turret_pivot.position;
                frankenstien_pivot.rotation = current_turret_pivot.rotation;
                // same step the card path takes: measure in the host PREFAB's units, so a host that
                // was spawned at a size its prefab does not have carries its turret with it instead
                // of having the mismatch absorbed into the scale factor
                NormaliseDonorToHost(frankenstien_pivot, __instance.transform, __instance.entityId);

                // match the new turret's size to the one it replaces, then align the meshes
                // (both measured before the old turret's renderers get disabled below)
                // (structural was measured above, before the old turret's animations were considered)
                if (ScaleTransplantedTurrets)
                    MatchTurretScale(current_turret_pivot, frankenstien_pivot, __instance.transform, structural);
                AlignTransplantedTurret(__instance.transform, current_turret_pivot, frankenstien_pivot, sit_on_top: structural);
                RecordSeating(__instance.entityId + " <- " + frankenstien_id, __instance.transform, frankenstien_pivot, preview: false);
                Mark(swap_timer, "scale+align");
                foreach (var pair in authored_scales){
                    if (pair.Key == null || pair.Key.transform.parent == null) continue;
                    float now = pair.Key.transform.parent.lossyScale.z;
                    if (now > 0.0001f && pair.Value > 0.0001f && Mathf.Abs(pair.Value / now - 1f) > 0.01f){
                        pair.Key.multiplier *= pair.Value / now;
                        if (log_details) RCMManager.Log("hit box '" + pair.Key.name + "' rescaled x" + (pair.Value / now).ToString("0.00") + " to keep its authored reach: " + __instance.entityId + " <- " + frankenstien_id);
                    }
                }

                // there are a few things i haven't fixed that prevent us from just deleting the old turret, especially with laser beam attacks
                //current_turret_pivot.SetParent(null);
                //GameObject.Destroy(current_turret_pivot.gameObject);
                // for now we simply disable the mesh & particle renderers - unless the pivot is the
                // unit's own torso (harvester bots aim with their whole upper body), which stays
                // visible with the donor gun seated on top
                if (!structural){
                    foreach (var r in current_turret_pivot.GetComponentsInChildren<Renderer>())
                        r.enabled = false;
                    foreach (var p in current_turret_pivot.GetComponentsInChildren<ParticleSystem>())
                        p.gameObject.SetActive(false);
                }

                // finally, cleanup the entity we stole the turret from
                GameObject.Destroy(frankenstien_entity_obj);
                Mark(swap_timer, "hide old+destroy");

                ReportTiming(swap_timer, "unit swap", __instance.entityId + " <- " + frankenstien_id);
                return true;
            }
        }


    }
}









// thanks AI, saving for if needed later to try resizing turrets to fit on their new unit
//public static class ScaleMatcher
//{
//    // --- Accurate vertex-based bounds ---
//    public static Bounds CalculateMeshBounds(GameObject root)
//    {
//        var filters = root.GetComponentsInChildren<MeshFilter>();
//        var rootTransform = root.transform;

//        bool initialized = false;
//        Bounds bounds = new Bounds();

//        foreach (var f in filters)
//        {
//            var mesh = f.sharedMesh;
//            if (!mesh) continue;

//            foreach (var v in mesh.vertices)
//            {
//                Vector3 world = f.transform.TransformPoint(v);
//                Vector3 local = rootTransform.InverseTransformPoint(world);

//                if (!initialized)
//                {
//                    bounds = new Bounds(local, Vector3.zero);
//                    initialized = true;
//                }
//                else
//                {
//                    bounds.Encapsulate(local);
//                }
//            }
//        }

//        return bounds;
//    }

//    // --- World-space size of mesh bounds ---
//    public static Vector3 GetWorldSize(GameObject obj)
//    {
//        Bounds local = CalculateMeshBounds(obj);
//        Vector3 size = Vector3.zero;

//        // Convert local bounds corners to world space
//        Vector3 min = obj.transform.TransformPoint(local.min);
//        Vector3 max = obj.transform.TransformPoint(local.max);

//        size = max - min;
//        return new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
//    }

//    // --- Compute uniform scale factor ---
//    public static float ComputeUniformScale(Vector3 targetSize, Vector3 sourceSize)
//    {
//        // Avoid divide-by-zero
//        if (sourceSize.x <= 0 || sourceSize.y <= 0 || sourceSize.z <= 0)
//            return 1f;

//        float sx = targetSize.x / sourceSize.x;
//        float sy = targetSize.y / sourceSize.y;
//        float sz = targetSize.z / sourceSize.z;

//        // Uniform scale = smallest axis ratio
//        return Mathf.Min(sx, sy, sz);
//    }

//    // --- Main function: scale replacement to match target ---
//    public static void MatchScale(GameObject target, GameObject replacement)
//    {
//        Vector3 targetSize = GetWorldSize(target);
//        Vector3 replacementSize = GetWorldSize(replacement);

//        float scaleFactor = ComputeUniformScale(targetSize, replacementSize);

//        replacement.transform.localScale *= scaleFactor;
//    }
//}
