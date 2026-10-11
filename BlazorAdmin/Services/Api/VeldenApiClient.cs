using BlazorAdmin.Models;

namespace BlazorAdmin.Services;

/// <summary>Velden, veldbeschikbaarheid, veldperiodes en veldtraining (#679, #581). HTTP, status- en foutafhandeling zit in <see cref="AdminApiTransport"/> (#1630).</summary>
public sealed class VeldenApiClient
{
    private readonly AdminApiTransport _t;

    public VeldenApiClient(AdminApiTransport transport) => _t = transport;

    // ── Velden (#679) ──

    public async Task<ApiResult<List<VeldDto>>> GetVeldenAsync()
        => await _t.GetAsync<List<VeldDto>>("api/beheer/velden");

    public async Task<ApiResult<object>> CreateVeldAsync(VeldDto dto)
        => await _t.PostAsync<object>("api/beheer/velden", dto);

    public async Task<ApiResult<object>> UpdateVeldAsync(int veldNummer, VeldDto dto)
        => await _t.PutAsync<object>($"api/beheer/velden/{veldNummer}", dto);

    // ── VeldBeschikbaarheid (#679: eerste GUI voor deze al bestaande API) ──

    public async Task<ApiResult<List<VeldBeschikbaarheidDto>>> GetVeldBeschikbaarheidAsync()
        => await _t.GetAsync<List<VeldBeschikbaarheidDto>>("api/beheer/veldbeschikbaarheid");

    public async Task<ApiResult<object>> CreateVeldBeschikbaarheidAsync(VeldBeschikbaarheidDto dto)
        => await _t.PostAsync<object>("api/beheer/veldbeschikbaarheid", dto);

    public async Task<ApiResult<object>> UpdateVeldBeschikbaarheidAsync(int id, VeldBeschikbaarheidDto dto)
        => await _t.PutAsync<object>($"api/beheer/veldbeschikbaarheid/{id}", dto);

    public async Task<ApiResult<object>> DeleteVeldBeschikbaarheidAsync(int id)
        => await _t.DeleteAsync<object>($"api/beheer/veldbeschikbaarheid/{id}");

    // ── VeldPeriode (#581: herbruikbare regimes zoals "Zomerstop" en "Competitie") ──

    public async Task<ApiResult<List<VeldPeriodeDto>>> GetVeldPeriodesAsync()
        => await _t.GetAsync<List<VeldPeriodeDto>>("api/beheer/veldperiodes");

    public async Task<ApiResult<object>> CreateVeldPeriodeAsync(VeldPeriodeDto dto)
        => await _t.PostAsync<object>("api/beheer/veldperiodes", dto);

    public async Task<ApiResult<object>> UpdateVeldPeriodeAsync(int id, VeldPeriodeDto dto)
        => await _t.PutAsync<object>($"api/beheer/veldperiodes/{id}", dto);

    public async Task<ApiResult<object>> DeleteVeldPeriodeAsync(int id)
        => await _t.DeleteAsync<object>($"api/beheer/veldperiodes/{id}");

    // ── VeldTraining (#679: trainingsschema per veld per weekdag) ──

    public async Task<ApiResult<List<VeldTrainingDto>>> GetVeldTrainingAsync()
        => await _t.GetAsync<List<VeldTrainingDto>>("api/beheer/veldtraining");

    public async Task<ApiResult<object>> CreateVeldTrainingAsync(VeldTrainingDto dto)
        => await _t.PostAsync<object>("api/beheer/veldtraining", dto);

    public async Task<ApiResult<object>> UpdateVeldTrainingAsync(int id, VeldTrainingDto dto)
        => await _t.PutAsync<object>($"api/beheer/veldtraining/{id}", dto);

    public async Task<ApiResult<object>> DeleteVeldTrainingAsync(int id)
        => await _t.DeleteAsync<object>($"api/beheer/veldtraining/{id}");
}
