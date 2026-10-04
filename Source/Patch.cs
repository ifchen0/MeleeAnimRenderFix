using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MeleeAnimRenderFix
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            new Harmony("ifchen0.meleeanimrenderfix").PatchAll();
            EventFactoryFix.Apply();
        }
    }

    /// <summary>
    /// AM.Events.EventBase derives from ScriptableObject, but its factory creates events with Activator.CreateInstance,
    /// so Unity warns "must be instantiated using the ScriptableObject.CreateInstance method" every time an animation's
    /// events are loaded. Swaps the factory delegates for ScriptableObject.CreateInstance.
    /// </summary>
    public static class EventFactoryFix
    {
        public static void Apply()
        {
            try
            {
                var allBases = (System.Collections.Generic.Dictionary<string, Func<AM.Events.EventBase>>)
                    AccessTools.Field(typeof(AM.Events.EventBase), "allBases").GetValue(null);
                foreach (Type type in typeof(AM.Events.EventBase).AllSubclassesNonAbstract())
                {
                    var probe = (AM.Events.EventBase)ScriptableObject.CreateInstance(type);
                    string id = probe.EventID;
                    UnityEngine.Object.Destroy(probe);
                    if (allBases.ContainsKey(id))
                        allBases[id] = () => (AM.Events.EventBase)ScriptableObject.CreateInstance(type);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Melee Animation Render Fix] Could not replace event factory: {e}");
            }
        }
    }

    /// <summary>
    /// Animations restored from a save already have hasStarted set, yet AnimRenderer.RegisterInt calls OnStart again,
    /// which only logs "Started twice!" and returns. Skips that redundant call quietly.
    /// </summary>
    [HarmonyPatch(typeof(AM.AnimRenderer), nameof(AM.AnimRenderer.OnStart))]
    public static class Patch_AnimRenderer_OnStart
    {
        public static bool Prefix(bool ___hasStarted) => !___hasStarted;
    }

    /// <summary>
    /// Melee Animation's RenderPawnAt prefix reads the cached PreRenderResults.parms without checking that they were filled.
    /// When they are default (pawn == null), PawnRenderTree.ParallelPreDraw throws a NullReferenceException every frame.
    /// This prefix runs first and fills the results the same way vanilla RenderPawnAt does, but only when Melee Animation
    /// is about to draw the pawn itself.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt))]
    public static class Patch_PawnRenderer_RenderPawnAt
    {
        private static readonly Type AmPatchType = AccessTools.TypeByName("AM.Patches.Patch_PawnRenderer_RenderPawnAt");
        private static readonly AccessTools.FieldRef<bool> AllowNext =
            AmPatchType == null ? null : AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(AmPatchType, "AllowNext"));

        private static readonly FieldInfo ResultsField = AccessTools.Field(typeof(PawnRenderer), "results");
        private static readonly Type ResultsType = ResultsField?.FieldType;
        private static readonly FieldInfo ValidField = ResultsType == null ? null : AccessTools.Field(ResultsType, "valid");
        private static readonly FieldInfo ParmsField = ResultsType == null ? null : AccessTools.Field(ResultsType, "parms");
        private static readonly MethodInfo GetPreRenderResults = AccessTools.Method(typeof(PawnRenderer), "ParallelGetPreRenderResults");
        private static readonly MethodInfo GetDrawParms = AccessTools.Method(typeof(PawnRenderer), "GetDrawParms");
        private static readonly PropertyInfo DefaultRenderFlagsNow = AccessTools.Property(typeof(PawnRenderer), "DefaultRenderFlagsNow");

        public static bool Prepare() =>
            AllowNext != null && ValidField != null && ParmsField != null && GetPreRenderResults != null
            && GetDrawParms != null && DefaultRenderFlagsNow != null;

        [HarmonyPriority(Priority.First + 100)]
        public static void Prefix(PawnRenderer __instance, Pawn ___pawn, Vector3 drawLoc, Rot4? rotOverride, bool neverAimWeapon)
        {
            if (!AllowNext())
                return;
            try
            {
                object results = ResultsField.GetValue(__instance);
                if ((bool)ValidField.GetValue(results) && ((PawnDrawParms)ParmsField.GetValue(results)).pawn != null)
                    return;

                __instance.EnsureGraphicsInitialized();
                results = GetPreRenderResults.Invoke(__instance, new object[] { drawLoc, rotOverride, neverAimWeapon, true });

                // Vanilla leaves parms empty for pawns hidden from the player; Melee Animation still draws them.
                if (((PawnDrawParms)ParmsField.GetValue(results)).pawn == null)
                {
                    var flags = (PawnRenderFlags)DefaultRenderFlagsNow.GetValue(__instance) | PawnRenderFlags.Clothes | PawnRenderFlags.Headgear;
                    object parms = GetDrawParms.Invoke(__instance, new object[] { drawLoc, 0f, ___pawn.Rotation, __instance.CurRotDrawMode, flags });
                    ParmsField.SetValue(results, parms);
                    ValidField.SetValue(results, true);
                }
                ResultsField.SetValue(__instance, results);
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Melee Animation Render Fix] Failed to prepare render results for {___pawn}: {e}", 0x4D41_5246);
            }
        }
    }

    /// <summary>
    /// Melee Animation's RenderPawnAt prefix calls PawnRenderTree.SetDirty on every draw of an animated pawn, which throws
    /// away the whole render tree and re-resolves every graphic (very costly with Humanoid Alien Races) several times per
    /// frame. The rebuild is only needed for a standalone severed head, which must be set up while HasHead is forced on.
    /// For everything else ParallelPreDraw already rebuilds its draw requests whenever flags, skip flags or facing change.
    /// </summary>
    [HarmonyPatch(typeof(AM.Patches.Patch_PawnRenderer_RenderPawnAt), nameof(AM.Patches.Patch_PawnRenderer_RenderPawnAt.Prefix))]
    public static class Patch_AmRenderPawnAt_SetDirty
    {
        private static readonly MethodInfo SetDirty = AccessTools.Method(typeof(PawnRenderTree), nameof(PawnRenderTree.SetDirty));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(SetDirty))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(Patch_AmRenderPawnAt_SetDirty), nameof(SetDirtyIfNeeded));
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                Log.Warning($"[Melee Animation Render Fix] Expected one SetDirty call in Melee Animation's RenderPawnAt prefix, found {replaced}.");
        }

        public static void SetDirtyIfNeeded(PawnRenderTree tree)
        {
            if (AM.Patches.Patch_PawnRenderer_RenderPawnAt.NextDrawMode == AM.Patches.Patch_PawnRenderer_RenderPawnAt.DrawMode.HeadStandalone)
                tree.SetDirty();
        }
    }

    /// <summary>
    /// PatchMaster.GetAnimator caches the last (pawn, renderer) pair in shared static fields and reads them back outside
    /// its lock. Vanilla calls it from the parallel pre-draw jobs (via IsPsychologicallyInvisible), so one thread can
    /// return another pawn's animator. That pawn is then pre-rendered as invisible for a frame: no shadow and drawn with
    /// the invisibility shader. Replaces it with the plain dictionary lookup, which is read-only during those jobs.
    /// </summary>
    [HarmonyPatch("AM.Patches.PatchMaster", "GetAnimator")]
    public static class Patch_PatchMaster_GetAnimator
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Pawn pawn, ref AM.AnimRenderer __result)
        {
            __result = AM.AnimRenderer.TryGetAnimator(pawn);
            return false;
        }
    }

    /// <summary>
    /// SweepMesh.Rebuild assigns vertices before indices without clearing the mesh. When an animation's time jumps back
    /// (e.g. a looping duel), PartWithSweep rebuilds the trail with fewer vertices than the old index buffer references,
    /// so Unity rejects the vertices ("Mesh.vertices is too small") and then the colors ("Mesh.colors is out of bounds").
    /// Clears the mesh first so the new arrays are always accepted.
    /// </summary>
    [HarmonyPatch(typeof(AM.Sweep.SweepMesh<AM.Sweep.PartWithSweep.Data>), nameof(AM.Sweep.SweepMesh<AM.Sweep.PartWithSweep.Data>.Rebuild))]
    public static class Patch_SweepMesh_Rebuild
    {
        public static void Prefix(AM.Sweep.SweepMesh<AM.Sweep.PartWithSweep.Data> __instance)
        {
            __instance.Mesh.Clear();
        }
    }

    /// <summary>
    /// AnimRenderer.Draw only honours delayedDestroy after drawing the pawns, so one draw exception keeps a finished
    /// animation alive forever. Destroys it anyway and lets the exception through for Melee Animation to log.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_AnimRenderer_Draw
    {
        private static readonly Type AnimRendererType = AccessTools.TypeByName("AM.AnimRenderer");
        private static readonly FieldInfo DelayedDestroy = AnimRendererType == null ? null : AccessTools.Field(AnimRendererType, "delayedDestroy");
        private static readonly MethodInfo Destroy = AnimRendererType == null ? null : AccessTools.Method(AnimRendererType, "Destroy");

        public static bool Prepare() => TargetMethod() != null && DelayedDestroy != null && Destroy != null;

        public static MethodBase TargetMethod() => AnimRendererType == null ? null : AccessTools.Method(AnimRendererType, "Draw");

        public static Exception Finalizer(object __instance, Exception __exception)
        {
            if (__exception != null && (bool)DelayedDestroy.GetValue(__instance))
                Destroy.Invoke(__instance, null);
            return __exception;
        }
    }
}
