using System;

namespace AP_Atlas.Core
{
    public enum RaceModeSetting
    {
        /// <summary>Restrictions apply in rooms the server reports as race mode.</summary>
        FollowServer,
        /// <summary>Restrictions always apply.</summary>
        AlwaysOn,
        /// <summary>Restrictions never apply.</summary>
        Off,
    }

    /// <summary>
    /// Race mode restrictions. When active for a slot, Atlas never asks the logic engine why a location is or
    /// isn't reachable; with "hide all logic" it also hides every in-logic indicator (Logic Tracker, map colors,
    /// hint logic, logic counts). Main thread only.
    /// </summary>
    public static class RaceRules
    {
        public static event Action Changed;

        private static AppSettings _settings;

        public static void Initialize(AppSettings settings) => _settings = settings;

        public static RaceModeSetting Mode => _settings?.RaceMode ?? RaceModeSetting.FollowServer;

        public static bool HideAllLogic => _settings?.RaceModeHidesAllLogic == true;

        /// <summary>Whether restrictions apply to a slot whose room does (or doesn't) run in race mode.</summary>
        public static bool IsActive(bool roomIsRace) => Mode switch
        {
            RaceModeSetting.AlwaysOn => true,
            RaceModeSetting.Off => false,
            _ => roomIsRace
        };

        /// <summary>Whether all in-logic information is hidden for such a slot.</summary>
        public static bool HidesLogic(bool roomIsRace) => IsActive(roomIsRace) && HideAllLogic;

        public static void SetMode(RaceModeSetting mode)
        {
            if (_settings == null || _settings.RaceMode == mode) return;
            _settings.RaceMode = mode;
            DataManager.SaveSettings(_settings);
            Changed?.Invoke();
        }

        public static void SetHideAllLogic(bool hide)
        {
            if (_settings == null || _settings.RaceModeHidesAllLogic == hide) return;
            _settings.RaceModeHidesAllLogic = hide;
            DataManager.SaveSettings(_settings);
            Changed?.Invoke();
        }
    }
}
