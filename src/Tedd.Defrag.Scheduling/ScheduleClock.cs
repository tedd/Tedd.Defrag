using Tedd.Defrag.Core;

namespace Tedd.Defrag.Scheduling;

public static class ScheduleClock
{
    public static bool IsDue(ScheduleDefinition schedule, DateTime localNow) => schedule.Enabled && !schedule.Template.LegacySimulation &&
        schedule.Days.Contains(localNow.DayOfWeek) && TimeOnly.FromDateTime(localNow) >= schedule.Time &&
        schedule.LastRun != DateOnly.FromDateTime(localNow);
}
