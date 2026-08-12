
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
        // or null to fall back to the built-in per-spawn random pick. selections outside the compat
        // list are ignored so external mods can't bypass MixNMatchUnits.txt
        public static Func<string, string> DonorSelector;
        public static IReadOnlyCollection<string> SupportedEntities => supported_entities;

        // scale transplanted turrets so their footprint roughly matches the turret they replace
        public static bool ScaleTransplantedTurrets = true;
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
        static void MatchTurretScale(Transform old_turret, Transform new_turret){
            float old_size = HorizontalFootprint(old_turret);
            float new_size = HorizontalFootprint(new_turret);
            if (old_size < 0.001f || new_size < 0.001f) return;

            float factor = old_size / new_size;
            if (factor > 0.8f && factor < 1.25f) return; // fits well enough already
            factor = Mathf.Clamp(factor, 0.5f, 2f);
            new_turret.localScale *= factor;
            RCMManager.Log($"scaled transplanted turret x{factor:F2} (old footprint {old_size:F1}, new {new_size:F1})");
        }
        static float HorizontalFootprint(Transform root){
            bool has_bounds = false;
            Bounds total = default;
            foreach (var r in root.GetComponentsInChildren<Renderer>()){
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!has_bounds){ total = r.bounds; has_bounds = true; }
                else total.Encapsulate(r.bounds);
            }
            if (!has_bounds) return 0f;
            return Mathf.Max(total.size.x, total.size.z);
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


        // this hook is called when a unit is created & manually initialized via the game, usually happening a few lines after the instantiation
        [HarmonyPatch(typeof(EntityController), "Init")]
        public static class Patch_EntityController_Init{
            [HarmonyPrefix]
            public static bool Prefix(EntityController __instance, EntityController originEntity){
                if (__instance.aiming == null 
                ||  __instance.skillAiming != null // skip units with skill aiming because im not sure if those units cause issues, technically this is a redundant check as we've already removed from unit compat txt
                || !supported_entities.Contains(__instance.entityId)) return true;

                // get current turret object from current unit
                Transform current_turret_pivot = GetPivotFromAiming(__instance.aiming);
                if (current_turret_pivot == null) return true; // this shouldn't be possible but as a failsafe...

                // remove current aiming components from original entity
                var comps = __instance.gameObject.GetComponents<SingleTargetAction>();
                foreach (var comp in comps) GameObject.Destroy(comp);

                // grab another unit to frankenstien onto
                // an external DonorSelector (e.g. a seeded randomizer) takes priority, otherwise
                // fall back to the built-in per-spawn random pick
                string frankenstien_id = DonorSelector?.Invoke(__instance.entityId);
                if (frankenstien_id == null || !supported_entities.Contains(frankenstien_id))
                    frankenstien_id = GetRandomSupportedEntity();
                RCMManager.Log("mixing units, base entityID: " + __instance.entityId + ", turret from: " + frankenstien_id);

                GameObject frankenstien_entity_obj = (GameObject)GameObject.Instantiate(Resources.Load(EntityBalancingStore.PrefabLocation(frankenstien_id)), new Vector3(0, 0, 0), Quaternion.identity);
                EntityController frankenstien_controller = frankenstien_entity_obj.GetComponent<EntityController>();

                // NOTE: for now we have to delete all of these components on the new turret because they have references that escape the turret gameobject
                // we could fix these up to reference the `__instance` variable instead, but haven't tested if this works or not
                ScaleByChangeableValue[] scaleables = frankenstien_entity_obj.GetComponentsInChildren<ScaleByChangeableValue>();
                foreach (var scaleable in scaleables) GameObject.Destroy (scaleable);

                // copy all of the aiming components from the new turret to current unit
                Transform frankenstien_pivot = GetPivotFromAiming(frankenstien_controller.aiming) ;
                List<SingleTargetAction> new_aiming_components = new List<SingleTargetAction>();
                CloneAimingComponentsTo(__instance, new_aiming_components, frankenstien_controller.aiming);
                if (new_aiming_components.Count == 0)       
                    __instance.aiming = null;
                else if (new_aiming_components.Count == 1)  
                    __instance.aiming = new_aiming_components[0];
                else { // set serial & populate entries
                    SerialSingleTargetAction new_action = __instance.gameObject.AddComponent<SerialSingleTargetAction>();
                    new_action.actions = new_aiming_components;
                    __instance.aiming = new_action;
                }

                // here we scrape the firing point and any useful animations from frankenstien controller to add to the current unit
                // also we copy the projectile firing event from here, it doesn't work perfect at the moment though
                Transform new_turrets_firingpoint = null;
                ShootProjectile new_fire_event = null;
                void ScrapeActionData(EntityEvent _event, IEntityAction action){
                    if (action.GetType() == typeof(ShootProjectile)){
                        ShootProjectile fireProjectileAction = (ShootProjectile)action;
                        new_turrets_firingpoint = fireProjectileAction.firePointsTransform;
                        if (new_fire_event == null || _event.@event == EntityController.Event.OnReadyToShoot)
                            new_fire_event = fireProjectileAction;

                    } else if (action.GetType() == typeof(Animate)){
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
                            ScrapeActionData(_event, action);
                    foreach (var action in _event.actions)
                        ScrapeActionData(_event, action);
                }
                // now assign firing point, if none then just make it something attached to the unit (so our new turret)
                if (new_turrets_firingpoint == null) new_turrets_firingpoint = frankenstien_pivot;


                bool should_inherit_projectile = true;
                bool CheckCopyShootLogic(IEntityAction action){
                    if (action.GetType() == typeof(ShootProjectile)){
                        ShootProjectile fireProjectileAction = (ShootProjectile)action;
                        if (should_inherit_projectile && new_fire_event != null){
                            new_fire_event.chooseTargetFromEntityIdentifier = false;
                            new_fire_event.multipleTargetEntityIdentifier = "";
                            if (!IsChildOf(new_fire_event.shotSoundAudioSource.transform, frankenstien_pivot))
                                new_fire_event.shotSoundAudioSource = fireProjectileAction.shotSoundAudioSource;
                            return true;
                        }
                        else fireProjectileAction.firePointsTransform = new_turrets_firingpoint;
                    }
                    return false;
                }
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
                            // see comments below
                            if (CheckCopyShootLogic(action))
                                conditional_action.actions[i] = new_fire_event;
                            if (CheckAndCleanAnimation(action)){
                                conditional_action.actions.RemoveAt(i);
                                i--;
                            }
                        }
                    }
                    for (int i = 0; i < _event.actions.Count; i++){
                        var action = _event.actions[i];
                        // if true, that means this action is suitable to be replaced by the found shoot action from the frankenstien turret
                        if (CheckCopyShootLogic(action))
                            _event.actions[i] = new_fire_event;
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

                // match the new turret's size to the one it replaces (measured before the old
                // turret's renderers get disabled below)
                if (ScaleTransplantedTurrets)
                    MatchTurretScale(current_turret_pivot, frankenstien_pivot);

                // there are a few things i haven't fixed that prevent us from just deleting the old turret, especially with laser beam attacks
                //current_turret_pivot.SetParent(null);
                //GameObject.Destroy(current_turret_pivot.gameObject);
                // for now we simply disable the mesh & particle renderers
                foreach (var r in current_turret_pivot.GetComponentsInChildren<Renderer>())
                    r.enabled = false;
                foreach (var p in current_turret_pivot.GetComponentsInChildren<ParticleSystem>())
                    p.gameObject.SetActive(false);

                // finally, cleanup the entity we stole the turret from
                GameObject.Destroy(frankenstien_entity_obj);

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
