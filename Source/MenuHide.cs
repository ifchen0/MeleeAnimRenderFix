using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AM.AMSettings;
using HarmonyLib;
using Verse;

namespace MeleeAnimRenderFix
{
    /// <summary>
    /// Toggles that hide Melee Animation's right-click menu entries. The fields are injected into Melee Animation's own
    /// settings window (Other tab) and saved in its settings file, so the field names double as XML keys there.
    /// Automatic lassos/executions are not affected; only the manual float menu is filtered.
    /// </summary>
    public static class MenuHideSettings
    {
        public static bool RenderFix_HideMenuWhenUndrafted;
        public static bool RenderFix_HideUnavailableMenuOptions;
        public static bool RenderFix_HideLassoMenuOptions;
        public static bool RenderFix_HideExecutionMenuOptions;
        public static bool RenderFix_HideSkillMenuOptions;

        private const string AnchorMember = "WarnOfFriendlyExecution";

        /// <summary>
        /// Adds the toggles to Melee Animation's settings holder right after <see cref="AnchorMember"/>, then re-reads
        /// its settings file so saved values reach the static fields (Melee Animation loaded its settings before us).
        /// </summary>
        public static void Inject()
        {
            try
            {
                Settings settings = AM.Core.Settings;
                var holder = (SimpleSettings.FieldHolder)AccessTools.Method(typeof(SimpleSettings), "GetHolder")
                    .Invoke(null, new object[] { settings });
                Type wrapperType = AccessTools.Inner(typeof(SimpleSettings), "MemberWrapperGen`1").MakeGenericType(typeof(bool));

                var ours = new List<KeyValuePair<MemberInfo, SimpleSettings.MemberWrapper>>();
                foreach (FieldInfo field in typeof(MenuHideSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType != typeof(bool))
                        continue;
                    var wrapper = (SimpleSettings.MemberWrapper)Activator.CreateInstance(wrapperType, settings, field);
                    ours.Add(new KeyValuePair<MemberInfo, SimpleSettings.MemberWrapper>(field, wrapper));
                }

                // Draw order is the dictionary's insertion order, so rebuild it with ours after the anchor.
                var existing = holder.Members.ToList();
                holder.Members.Clear();
                bool inserted = false;
                foreach (var pair in existing)
                {
                    holder.Members.Add(pair.Key, pair.Value);
                    if (!inserted && pair.Value.Name == AnchorMember)
                    {
                        foreach (var o in ours)
                            holder.Members.Add(o.Key, o.Value);
                        inserted = true;
                    }
                }
                if (!inserted)
                {
                    foreach (var o in ours)
                        holder.Members.Add(o.Key, o.Value);
                }

                Mod core = LoadedModManager.GetMod<AM.Core>();
                LoadedModManager.ReadModSettings<Settings>(core.Content.FolderName, core.GetType().Name);
            }
            catch (Exception e)
            {
                Log.Warning($"[Melee Animation Render Fix] Could not add menu options to Melee Animation settings: {e.InnerException?.Message ?? e.Message}");
            }
        }
    }

    /// <summary>Remembers which float menu options are lasso or execution entries; everything else is a skill entry.</summary>
    public static class MenuOptionKinds
    {
        public static readonly object Lasso = new object();
        public static readonly object Execution = new object();
        public static readonly ConditionalWeakTable<FloatMenuOption, object> Tags = new ConditionalWeakTable<FloatMenuOption, object>();

        public static void Tag(FloatMenuOption option, object kind)
        {
            if (option != null)
            {
                Tags.Remove(option);
                Tags.Add(option, kind);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_DraftedFloatMenuOptionsUI_LassoOptions
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AM.UI.DraftedFloatMenuOptionsUI), "GetEnabledLassoOption");
            yield return AccessTools.Method(typeof(AM.UI.DraftedFloatMenuOptionsUI), "GetDisabledLassoOption");
        }

        public static void Postfix(FloatMenuOption __result) => MenuOptionKinds.Tag(__result, MenuOptionKinds.Lasso);
    }

    [HarmonyPatch]
    public static class Patch_DraftedFloatMenuOptionsUI_ExecutionOptions
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AM.UI.DraftedFloatMenuOptionsUI), "GetEnabledExecuteOption");
            yield return AccessTools.Method(typeof(AM.UI.DraftedFloatMenuOptionsUI), "GetDisabledExecutionOption");
        }

        public static void Postfix(FloatMenuOption __result) => MenuOptionKinds.Tag(__result, MenuOptionKinds.Execution);
    }

    /// <summary>Filters Melee Animation's right-click entries according to <see cref="MenuHideSettings"/>.</summary>
    [HarmonyPatch(typeof(AM.UI.DraftedFloatMenuOptionsUI), nameof(AM.UI.DraftedFloatMenuOptionsUI.GenerateMenuOptions))]
    public static class Patch_DraftedFloatMenuOptionsUI_GenerateMenuOptions
    {
        public static IEnumerable<FloatMenuOption> Postfix(IEnumerable<FloatMenuOption> __result, Pawn pawn)
        {
            // Skipping the enumeration also skips Melee Animation's report generation.
            if (MenuHideSettings.RenderFix_HideMenuWhenUndrafted && pawn != null && !pawn.Drafted)
                yield break;

            foreach (FloatMenuOption option in __result)
            {
                if (!ShouldHide(option))
                    yield return option;
            }
        }

        private static bool ShouldHide(FloatMenuOption option)
        {
            if (option == null)
                return true;
            if (MenuHideSettings.RenderFix_HideUnavailableMenuOptions && option.Disabled)
                return true;
            MenuOptionKinds.Tags.TryGetValue(option, out object kind);
            if (kind == MenuOptionKinds.Lasso)
                return MenuHideSettings.RenderFix_HideLassoMenuOptions;
            if (kind == MenuOptionKinds.Execution)
                return MenuHideSettings.RenderFix_HideExecutionMenuOptions;
            return MenuHideSettings.RenderFix_HideSkillMenuOptions;
        }
    }
}
