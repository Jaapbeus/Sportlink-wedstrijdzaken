using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner
{
    /// <summary>
    /// Facade — delegeert alle aanroepen naar de specifieke use-case services.
    /// Bestaande callers (PlannerFunctions) hoeven niet te worden aangepast. (#475)
    ///
    /// Service-verdeling:
    ///   AvailabilityService  — CheckAvailabilityAsync, CheckDoordeweeksBeschikbaarAsync
    ///   AutoPlanService      — AutoPlanAsync, AutoPlanToepassenAsync
    ///   OptimizationService  — OptimaliseerAsync
    ///   RescheduleService    — CheckRescheduleAvailabilityAsync
    ///   TeamScheduleService  — GetTeamScheduleAsync
    ///
    /// Gedeelde utilities en de FieldScheduler engine:
    ///   PlannerShared        — constanten, helpers, FieldScheduler, CandidateSlot, IngeplandSlot
    /// </summary>
    public static class PlannerService
    {
        public static Task<CheckAvailabilityResponse> CheckAvailabilityAsync(
            CheckAvailabilityRequest request, ILogger log, string? clubCode = null)
            => AvailabilityService.CheckAvailabilityAsync(request, log, clubCode);

        public static Task<DoordeweeksBeschikbaarResponse> CheckDoordeweeksBeschikbaarAsync(
            DoordeweeksBeschikbaarRequest request, ILogger log, string? clubCode = null)
            => AvailabilityService.CheckDoordeweeksBeschikbaarAsync(request, log, clubCode);

        public static Task<AutoPlanResponse> AutoPlanAsync(
            AutoPlanRequest request, string clubCode, ILogger log)
            => AutoPlanService.AutoPlanAsync(request, clubCode, log);

        public static Task<AutoPlanToepassenResponse> AutoPlanToepassenAsync(
            AutoPlanToepassenRequest request, string clubCode, ILogger log)
            => AutoPlanService.AutoPlanToepassenAsync(request, clubCode, log);

        public static Task<List<VeldbezettingItem>> VeldbezettingAsync(DateOnly datum, string clubCode)
            => AutoPlanService.VeldbezettingAsync(datum, clubCode);

        public static Task<OptimaliseerResponse> OptimaliseerAsync(
            OptimaliseerRequest request, string? clubCode, ILogger log)
            => OptimizationService.OptimaliseerAsync(request, clubCode, log);

        public static Task<HerplanCheckResponse> CheckRescheduleAvailabilityAsync(
            HerplanCheckRequest request, ILogger log, string? clubCode = null)
            => RescheduleService.CheckRescheduleAvailabilityAsync(request, log, clubCode);

        public static Task<TeamScheduleResponse?> GetTeamScheduleAsync(string team, string? clubCode = null)
            => TeamScheduleService.GetTeamScheduleAsync(team, clubCode);
    }
}
