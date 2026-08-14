
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
        
        // uniform-scale the transplanted turret so its horizontal footprint roughly matches the
        // old turret's. renderer AABBs instead of per-vertex bounds (cheap and good enough for
        // a footprint), particles/trails ignored, a no-op deadzone because most turrets already
        // fit reasonably, and a hard clamp so nothing degenerates
        // However snug the gun sits against the part it replaces, it must also fit the BODY it
        // lands on: a turret bigger than its chassis reads as the chassis being an accessory of
        // the gun (the harvester with a lance several times its own size).
        const float ChassisCapRatio = 1.15f;

        // The one rule for how big a transplanted turret gets. Aims SMALLER than the old turret (a
        // snug gun reads better than a bulky one), leaves a good-enough fit alone, and clamps
        // asymmetrically: growing is capped hard because a grown gun dominates the silhouette,
        // shrinking barely at all - long lance donors legitimately need x0.15 to sit on a small
        // bot, and the old 0.35 floor is exactly why they shipped oversized.
        public static float TurretScaleFactor(float old_size, float new_size, float target = 0.85f){
            if (old_size < 0.001f || new_size < 0.001f) return 1f;
            float factor = old_size / new_size * target;
            if (factor > 0.9f && factor < 1.1f) return 1f; // fits well enough already
            return Mathf.Clamp(factor, 0.12f, 2.5f);
        }

        static float ApplyChassisCap(float factor, float new_size, Transform unit_root, Transform exclude_a, Transform exclude_b){
            if (!TryGetMeshBounds(unit_root, out Bounds chassis_b, exclude_a, exclude_b)) return factor;
            float chassis = Mathf.Max(chassis_b.size.x, chassis_b.size.z);
            if (chassis < 0.001f || new_size < 0.001f) return factor;
            return Mathf.Min(factor, Mathf.Max(0.05f, ChassisCapRatio * chassis / new_size));
        }

        // Harvester-style bots aim with their whole upper body: the pivot the aiming drives IS the
        // torso, so hiding it beheads the model (the reported headless harvester). A pivot that
        // carries most of the unit's own silhouette is treated as structure: kept visible, with
        // the donor gun seated on top of it instead of in its place.
        static bool PivotIsStructural(Transform unit_root, Transform pivot, Transform donor_pivot){
            if (!TryGetMeshBounds(pivot, out Bounds pivot_b)) return false;
            if (!TryGetMeshBounds(unit_root, out Bounds unit_b, donor_pivot)) return false;
            float pivot_size = Mathf.Max(pivot_b.size.x, pivot_b.size.z);
            float unit_size = Mathf.Max(unit_b.size.x, unit_b.size.z);
            if (unit_size < 0.001f || pivot_size / unit_size <= 0.55f) return false;
            // Footprint alone is not enough: the support tank's long medic gun spans over half the
            // unit but starts high on the hull, and treating it as structure kept it AND stacked
            // the donor on top - two turrets. A torso reaches DOWN into the body; a gun, however
            // long, sits on top of it. Only a pivot whose mesh starts in the lower part of the
            // unit is really structure.
            return pivot_b.min.y < unit_b.min.y + 0.4f * unit_b.size.y;
        }

        static void MatchTurretScale(Transform old_turret, Transform new_turret, Transform unit_root, bool structural){
            float old_size = HorizontalFootprint(old_turret);
            float new_size = HorizontalFootprint(new_turret);
            // a gun RIDING the torso should stay clearly smaller than it; one REPLACING a turret
            // matches it snugly
            float factor = TurretScaleFactor(old_size, new_size, structural ? 0.6f : 0.85f);
            factor = ApplyChassisCap(factor, new_size, unit_root, old_turret, new_turret);
            if (Mathf.Abs(factor - 1f) < 0.0001f) return;
            new_turret.localScale *= factor;
            RCMManager.Log($"scaled transplanted turret x{factor:F2} (old footprint {old_size:F1}, new {new_size:F1}{(structural ? ", torso mount" : "")})");
        }

        // Previews are the same swap seen through a card-scaled hierarchy, and they have to show
        // the unit the player will actually get. Fitting the donor exactly to the card's own turret
        // was wrong: the in-world factor is CLAMPED, so a donor that could not be grown enough on
        // the battlefield still appeared perfectly fitted on the card, and the card read as a
        // different unit. Reproduce the WORLD proportions instead — take the factor the in-world
        // swap uses (clamp, chassis cap and all), measured on world-scale prefabs, and carry it
        // into this hierarchy through the card's own scale ratio.
        static void MatchTurretScaleForPreview(string base_entity_id, Transform old_turret, Transform new_turret, bool structural){
            float card_old = HorizontalFootprint(old_turret);
            float donor_size = HorizontalFootprint(new_turret); // donor is instantiated at world scale
            if (card_old < 0.0001f || donor_size < 0.0001f) return;
            float target = structural ? 0.6f : 0.85f;

            var world = WorldFootprintsOf(base_entity_id);
            float factor;
            if (world.turret > 0.001f){
                float world_factor = TurretScaleFactor(world.turret, donor_size, target);
                if (world.chassis > 0.001f)
                    world_factor = Mathf.Min(world_factor, Mathf.Max(0.05f, ChassisCapRatio * world.chassis / donor_size));
                factor = world_factor * (card_old / world.turret); // card_old / world.turret = the card's shrink
            } else {
                // probe failed: a snug exact fit beats what this path used to do here, which was
                // NOTHING - a world-scale lance left towering over a card-scale chassis
                factor = card_old / donor_size * target;
            }
            if (factor < 0.0001f || Mathf.Abs(factor - 1f) < 0.0001f) return;
            new_turret.localScale *= factor;
        }

        // Turret and chassis footprints of a unit at world scale, measured off the prefab.
        // Cached: this costs an instantiate, and previews are rebuilt constantly while browsing.
        struct WorldFootprints { public float turret, chassis; }
        static readonly Dictionary<string, WorldFootprints> world_footprints = new Dictionary<string, WorldFootprints>();
        static WorldFootprints WorldFootprintsOf(string entity_id){
            if (world_footprints.TryGetValue(entity_id, out var cached)) return cached;
            var result = new WorldFootprints();
            GameObject probe = null;
            try{
                var prefab = Resources.Load(EntityBalancingStore.PrefabLocation(entity_id));
                if (prefab != null){
                    probe = (GameObject)GameObject.Instantiate(prefab, new Vector3(0f, -10000f, 0f), Quaternion.identity);
                    var controller = probe.GetComponent<EntityController>();
                    Transform pivot = (controller == null || controller.aiming == null) ? null : GetPivotFromAiming(controller.aiming);
                    if (pivot != null) result.turret = HorizontalFootprint(pivot);
                    if (TryGetMeshBounds(probe.transform, out Bounds chassis_b, pivot))
                        result.chassis = Mathf.Max(chassis_b.size.x, chassis_b.size.z);
                }
            } catch (Exception e){ RCMManager.Log("world footprint probe failed for " + entity_id + ": " + e.Message); }
            finally { if (probe != null) GameObject.Destroy(probe); }
            world_footprints[entity_id] = result;
            return result;
        }

        static float HorizontalFootprint(Transform root){
            if (!TryGetMeshBounds(root, out Bounds total)) return 0f;
            // height counts too: a tower of a turret on a flat chassis looks as wrong as a wide one
            return Mathf.Max(total.size.x, total.size.z, total.size.y * 0.8f);
        }

        // the swap positions the donor PIVOT at the old pivot, but a donor's mesh can sit far away
        // from its own pivot (tall donor chassis), leaving the gun floating next to the new body.
        // so move the transplanted pivot until the new turret's mesh sits where the old one's was:
        // centered on it horizontally, resting at the same base height
        // The single largest visible mesh part. Combined AABBs lied to us twice: an antenna extends
        // max.y so guns mounted "on top" hovered at antenna height, and it drags center.x sideways
        // so they drifted toward the antenna. The biggest block IS the torso/hull for every unit
        // that matters, and thin tall parts cannot skew it.
        static bool TryGetDominantBounds(Transform root, out Bounds best, Transform exclude_a = null, Transform exclude_b = null){
            best = default;
            float best_volume = -1f;
            foreach (var r in root.GetComponentsInChildren<Renderer>()){
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!r.enabled) continue;
                if (exclude_a != null && IsChildOf(r.transform, exclude_a)) continue;
                if (exclude_b != null && IsChildOf(r.transform, exclude_b)) continue;
                Bounds b = r.bounds;
                float volume = b.size.x * b.size.y * b.size.z;
                if (volume > best_volume){ best_volume = volume; best = b; }
            }
            return best_volume > 0f;
        }

        static void AlignTransplantedTurret(Transform unit_root, Transform old_turret, Transform new_turret, bool sit_on_top = false){
            if (!TryGetMeshBounds(new_turret, out Bounds new_b)) return;
            // anchor on the old part's dominant block, not its combined bounds
            if (!TryGetDominantBounds(old_turret, out Bounds anchor)){
                if (!TryGetMeshBounds(old_turret, out anchor)) return;
            }
            // replacing a turret: rest at its base. riding a kept torso: sink into its top by a
            // third of the smaller height, so the mount connects - a shoulder cannon, not a balloon
            float target_y = sit_on_top
                ? anchor.max.y + new_b.extents.y - Mathf.Min(new_b.size.y, anchor.size.y) * 0.35f
                : anchor.min.y + new_b.extents.y;
            Vector3 target = new Vector3(anchor.center.x, target_y, anchor.center.z);
            Vector3 offset = target - new_b.center;
            if (offset.sqrMagnitude > 0.0001f) new_turret.position += offset;

            // Contact clamp: whatever the pivot bounds claimed (a tiny emitter halfway up a mast, a
            // pole), the gun must touch the unit's main body. If its underside still hangs above
            // the hull block's top, pull it down into it. NOT for torso mounts: there the anchor IS
            // the torso and already guarantees contact, while "the body without both pivots" is
            // just the legs - clamping against those buried the harvester's shoulder gun.
            if (!sit_on_top
                && TryGetDominantBounds(unit_root, out Bounds body, old_turret, new_turret)
                && TryGetMeshBounds(new_turret, out Bounds seated)
                && seated.min.y > body.max.y){
                float drop = seated.min.y - (body.max.y - 0.15f * seated.size.y);
                new_turret.position += Vector3.down * drop;
                RCMManager.Log($"contact clamp pulled turret down by {drop:F2}");
            }
            RCMManager.Log($"aligned transplanted turret by {offset.magnitude:F2}{(sit_on_top ? " (onto torso)" : "")}");
        }

        static bool TryGetMeshBounds(Transform root, out Bounds total, Transform exclude_a = null, Transform exclude_b = null){
            total = default;
            List<Bounds> parts = new List<Bounds>();
            foreach (var r in root.GetComponentsInChildren<Renderer>()){
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!r.enabled) continue;
                if (exclude_a != null && IsChildOf(r.transform, exclude_a)) continue;
                if (exclude_b != null && IsChildOf(r.transform, exclude_b)) continue;
                parts.Add(r.bounds);
            }
            if (parts.Count == 0) return false;

            // beam/effect meshes are stretched towards their target and report enormous world
            // bounds (one turret measured 215657 units), which would wreck scale & alignment.
            // so drop anything far bigger than the typical part before combining
            List<float> sizes = parts.Select(b => Mathf.Max(b.size.x, b.size.y, b.size.z)).OrderBy(v => v).ToList();
            float limit = Mathf.Max(0.001f, sizes[sizes.Count / 2] * 4f);
            bool has_bounds = false;
            foreach (var b in parts){
                if (Mathf.Max(b.size.x, b.size.y, b.size.z) > limit) continue;
                if (!has_bounds){ total = b; has_bounds = true; }
                else total.Encapsulate(b);
            }
            if (!has_bounds) total = parts[0];
            return true;
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
                    if (!supported_entities.Contains(entityId)) return;
                    string donor_id = DonorSelector(entityId);
                    if (string.IsNullOrEmpty(donor_id) || !supported_entities.Contains(donor_id)) return;
                    var timer = StartTiming();
                    ApplyVisualSwap(__result, entityId, donor_id);
                    ReportTiming(timer, "preview swap", entityId + " <- " + donor_id);
                } catch (Exception e){ RCMManager.Log("preview turret swap failed: " + e.Message); }
            }
        }

        // timing helper shared by the swap paths
        static System.Diagnostics.Stopwatch StartTiming(){
            return LogSwapsSlowerThanMs > 0 ? System.Diagnostics.Stopwatch.StartNew() : null;
        }
        static void ReportTiming(System.Diagnostics.Stopwatch watch, string what, string detail){
            if (watch == null) return;
            watch.Stop();
            double ms = watch.Elapsed.TotalMilliseconds;
            if (ms >= LogSwapsSlowerThanMs)
                RCMManager.Log($"MixNMatch PERF: {what} took {ms:F1}ms ({detail})");
        }
        static void ApplyVisualSwap(GameObject display_model, string base_entity_id, string donor_id){
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
                if (PivotIsStructural(donor_obj.transform, new_pivot, null)) return;

                new_pivot.SetParent(old_pivot.parent);
                new_pivot.position = old_pivot.position;
                new_pivot.rotation = old_pivot.rotation;
                // display model only: strip everything but the meshes so nothing ticks or reacts
                foreach (var comp in new_pivot.GetComponentsInChildren<Component>(true)){
                    if (comp is Transform || comp is MeshFilter || comp is MeshRenderer || comp is SkinnedMeshRenderer) continue;
                    GameObject.Destroy(comp);
                }
                bool structural = PivotIsStructural(display_model.transform, old_pivot, new_pivot);
                MatchTurretScaleForPreview(base_entity_id, old_pivot, new_pivot, structural);
                AlignTransplantedTurret(display_model.transform, old_pivot, new_pivot, sit_on_top: structural);
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
                || !supported_entities.Contains(__instance.entityId)) return true;

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
                RCMManager.Log("mixing units, base entityID: " + __instance.entityId + ", turret from: " + frankenstien_id);

                GameObject frankenstien_entity_obj = (GameObject)GameObject.Instantiate(Resources.Load(EntityBalancingStore.PrefabLocation(frankenstien_id)), new Vector3(0, 0, 0), Quaternion.identity);
                EntityController frankenstien_controller = frankenstien_entity_obj.GetComponent<EntityController>();

                // whether a unit charges in or shoots from afar belongs to the weapon, not the chassis:
                // a transplanted melee weapon (poker) on a ranged chassis would otherwise be swung
                // from across the map. Init builds EntityAttack from these fields right after this
                // prefix, so setting them here is enough. the extension is padded a little because
                // the new chassis reaches from its own collision radius
                if (__instance.melee != frankenstien_controller.melee)
                    RCMManager.Log("weapon is " + (frankenstien_controller.melee ? "melee" : "ranged") + ", switching " + __instance.entityId + " to match");
                __instance.melee = frankenstien_controller.melee;
                __instance.meleeRadiusExtension = frankenstien_controller.melee
                    ? frankenstien_controller.meleeRadiusExtension + 0.5f
                    : frankenstien_controller.meleeRadiusExtension;

                // NOTE: for now we have to delete all of these components on the new turret because they have references that escape the turret gameobject
                // we could fix these up to reference the `__instance` variable instead, but haven't tested if this works or not
                ScaleByChangeableValue[] scaleables = frankenstien_entity_obj.GetComponentsInChildren<ScaleByChangeableValue>();
                foreach (var scaleable in scaleables)
                    scaleable.entityController = __instance;
                    //GameObject.Destroy (scaleable);

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
                    if (PivotIsStructural(frankenstien_entity_obj.transform, frankenstien_pivot, null))
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
                    RCMManager.Log("skipping swap for " + __instance.entityId + " <- " + frankenstien_id + ": " + e.Message);
                    return true;
                }
                // clone succeeded: NOW retire the old aiming components
                foreach (var comp in old_aiming_comps) GameObject.Destroy(comp);
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
                        if (valid_tranforms > 1){
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
                        if (!IsChildOfOrCopyTopLevelChild(ident.scaledOverlapBox?.gameObject?.transform, true)){
                            ident.scaledOverlapBox = null;
                            ident.radius = EntityIdentifier.Radius.SelfWeaponRange;
                            RCMManager.Log("had to null out scaled overlap box on entity ident: '" + ident.name + "' from: " + __instance.entityId + " <- " + frankenstien_id);
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


                // this is technically redundant as we dont destroy any of the turret pieces for now
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
                                }}
                                b--;
                        }}
                        return (fireProjectileAction.transforms.Count == 0);
                        // we could also then remove the event if it has no actions left too, but that has yet to cause any issues
                    }
                    return false;
                }
                foreach (var _event in __instance.events){
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

                // match the new turret's size to the one it replaces, then align the meshes
                // (both measured before the old turret's renderers get disabled below)
                bool structural = PivotIsStructural(__instance.transform, current_turret_pivot, frankenstien_pivot);
                if (ScaleTransplantedTurrets)
                    MatchTurretScale(current_turret_pivot, frankenstien_pivot, __instance.transform, structural);
                AlignTransplantedTurret(__instance.transform, current_turret_pivot, frankenstien_pivot, sit_on_top: structural);

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
