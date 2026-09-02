using Microsoft.Extensions.Logging;

namespace SportlinkFunction.Planner
{
    /// <summary>
    /// Facade — delegeert alle aanroepen naar de specifieke use-case services.
    /// Bestaande callers (PlannerFunctions) hoeven niet te worden aangepast. (#475)
    ///
    /// Service-verdeling:
    ///   AvailabilityService  — CheckAvailabilityAsync, CheckDoordeweeksBeschikbaarAsync
    ///   AutoPlanService      — AutoPlanAsync, AutoPlanToepassenAsync  (de enige dagplanning-optimalisatie)
    ///   RescheduleService    — CheckRescheduleAvailabilityAsync
    ///   TeamScheduleService  — GetTeamScheduleAsync
    ///
    /// De losse OptimizationService (endpoint /planner/optimaliseer, "klassiek optimaliseren") is
    /// vervallen bij #666: twee optimalisatiepaden naast elkaar met verschillend gedrag — het klassieke
    /// pad negeerde voorkeurstijden en prioriteiten volledig — leidde tot planningen die de ingestelde
    /// voorkeuren niet respecteerden. AutoPlanService is nu de enige optimalisatie.
    ///
    /// Gedeelde utilities en de FieldScheduler engine:
    ///   Planner.Shared.PlannerShared — constanten, helpers, FieldScheduler, CandidateSlot,
    ///                          IngeplandSlot — verhuisd naar Planner.Shared (#888), tier-agnostisch
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

        public static Task<HerplanCheckResponse> CheckRescheduleAvailabilityAsync(
            HerplanCheckRequest request, ILogger log, string? clubCode = null)
            => RescheduleService.CheckRescheduleAvailabilityAsync(request, log, clubCode);

        public static Task<TeamScheduleResponse?> GetTeamScheduleAsync(string team, string? clubCode = null)
            => TeamScheduleService.GetTeamScheduleAsync(team, clubCode);
    }
}
