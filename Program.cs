using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

bool picoGKAvailable = System.IO.File.Exists("/usr/local/lib/libpicogk.so");
string activeEngine = picoGKAvailable ? "PicoGK" : "CEM-MarchingCubes";

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = activeEngine,
    picoGK = picoGKAvailable,
    version = "2.0.0",
    capabilities = new[] { "tpms", "lattice", "cem" },
    timestamp = DateTime.UtcNow
}));

app.MapPost("/api/tpms", (TPMSRequest req) =>
{
    try
    {
        int res = (req.Resolution ?? "medium") switch { "low" => 60, "medium" => 100, "high" => 160, _ => 100 };
        double sx = req.BoundingBox != null && req.BoundingBox.Length > 0 ? req.BoundingBox[0] : 30;
        double sy = req.BoundingBox != null && req.BoundingBox.Length > 1 ? req.BoundingBox[1] : 30;
        double sz = req.BoundingBox != null && req.BoundingBox.Length > 2 ? req.BoundingBox[2] : 30;
        byte[] stl = MathSTLGenerator.GenerateTPMS(req.SurfaceType ?? "gyroid", req.CellSize > 0 ? req.CellSize : 8, req.WallThickness > 0 ? req.WallThickness : 0.8, sx, sy, sz, res);
        int triCount = BitConverter.ToInt32(stl, 80);
        return Results.Ok(new { success = true, engine = activeEngine, stlBase64 = Convert.ToBase64String(stl), triangles = triCount, fileSize = stl.Length });
    }
    catch (Exception ex) { return Results.Problem(title: "TPMS failed", detail: ex.Message, statusCode: 500); }
});

app.MapPost("/api/lattice", (LatticeRequest req) =>
{
    try
    {
        int res = (req.Resolution ?? "medium") switch { "low" => 50, "medium" => 80, "high" => 120, _ => 80 };
        double sx = req.BoundingBox != null && req.BoundingBox.Length > 0 ? req.BoundingBox[0] : 30;
        double sy = req.BoundingBox != null && req.BoundingBox.Length > 1 ? req.BoundingBox[1] : 30;
        double sz = req.BoundingBox != null && req.BoundingBox.Length > 2 ? req.BoundingBox[2] : 30;
        byte[] stl = MathSTLGenerator.GenerateLattice(req.UnitCell ?? "octet", req.StrutDiameter > 0 ? req.StrutDiameter : 2, sx, sy, sz, res);
        int triCount = BitConverter.ToInt32(stl, 80);
        return Results.Ok(new { success = true, engine = activeEngine, stlBase64 = Convert.ToBase64String(stl), triangles = triCount, fileSize = stl.Length });
    }
    catch (Exception ex) { return Results.Problem(title: "Lattice failed", detail: ex.Message, statusCode: 500); }
});

app.MapPost("/api/generate", (GenerateRequest req) =>
{
    try
    {
        string type = (req.Type ?? "tpms").ToLower();
        int res = (req.Resolution ?? "medium") switch { "low" => 60, "medium" => 100, "high" => 160, _ => 100 };
        double sx = req.BoundingBox != null && req.BoundingBox.Length > 0 ? req.BoundingBox[0] : 30;
        double sy = req.BoundingBox != null && req.BoundingBox.Length > 1 ? req.BoundingBox[1] : 30;
        double sz = req.BoundingBox != null && req.BoundingBox.Length > 2 ? req.BoundingBox[2] : 30;
        byte[] stl;
        if (type == "lattice") stl = MathSTLGenerator.GenerateLattice(req.SurfaceType ?? "octet", req.CellSize > 0 ? req.CellSize : 2, sx, sy, sz, res);
        else stl = MathSTLGenerator.GenerateTPMS(req.SurfaceType ?? "gyroid", req.CellSize > 0 ? req.CellSize : 8, req.WallThickness > 0 ? req.WallThickness : 0.8, sx, sy, sz, res);
        int triCount = BitConverter.ToInt32(stl, 80);
        return Results.Ok(new { success = true, engine = activeEngine, stlBase64 = Convert.ToBase64String(stl), triangles = triCount, fileSize = stl.Length });
    }
    catch (Exception ex) { return Results.Problem(title: "Generation failed", detail: ex.Message, statusCode: 500); }
});

app.MapPost("/api/cem", (CEMRequest req) =>
{
    try
    {
        var model = new CEMGenerator.CEMModel
        {
            Name = req.Name ?? "Part",
            Envelope = req.Envelope ?? new[] { 40.0, 40.0, 40.0 },
            ShellThickness = req.ShellThickness,
            Resolution = req.Resolution ?? "low",
            Fill = new CEMGenerator.CEMFill
            {
                Type = req.Fill?.Type ?? "gyroid",
                Splitting = req.Fill?.Splitting ?? "full_wall",
                CellSizeMin = req.Fill?.CellSizeMin ?? 5,
                CellSizeMax = req.Fill?.CellSizeMax ?? req.Fill?.CellSizeMin ?? 5,
                WallThicknessMin = req.Fill?.WallThicknessMin ?? 0.8,
                WallThicknessMax = req.Fill?.WallThicknessMax ?? req.Fill?.WallThicknessMin ?? 0.8,
                Modulation = req.Fill?.Modulation ?? "uniform"
            },
            Manufacturing = new CEMGenerator.CEMManufacturing
            {
                Process = req.Manufacturing?.Process ?? "SLS",
                Material = req.Manufacturing?.Material ?? "PA12",
                MinWall = req.Manufacturing?.MinWall ?? 0.6,
                MaxOverhang = req.Manufacturing?.MaxOverhang ?? 45
            }
        };
        if (req.Regions != null)
        {
            model.Regions = req.Regions.Select(r => new CEMGenerator.CEMRegion
            {
                Name = r.Name ?? "", Requirement = r.Requirement ?? "lattice",
                Offset = r.Offset, Size = r.Size, Shape = r.Shape ?? "box",
                Radius = r.Radius, HeatFlux = r.HeatFlux
            }).ToArray();
        }
        byte[] stl = CEMGenerator.Generate(model);
        int triCount = BitConverter.ToInt32(stl, 80);
        return Results.Ok(new
        {
            success = true, engine = "CEM-" + activeEngine, mode = "cem",
            stlBase64 = Convert.ToBase64String(stl), triangles = triCount, fileSize = stl.Length,
            model = new
            {
                name = model.Name, envelope = model.Envelope, fillType = model.Fill.Type,
                modulation = model.Fill.Modulation,
                cellSizeRange = new[] { model.Fill.CellSizeMin, model.Fill.CellSizeMax },
                wallThicknessRange = new[] { model.Fill.WallThicknessMin, model.Fill.WallThicknessMax },
                splitting = model.Fill.Splitting, regionsCount = model.Regions?.Length ?? 0
            }
        });
    }
    catch (Exception ex) { return Results.Problem(title: "CEM failed", detail: ex.Message, statusCode: 500); }
});

app.MapGet("/api/cem/schema", () => Results.Ok(new
{
    fillTypes = new[] { "gyroid", "schwarz_p", "schwarz_d", "diamond", "lidinoid", "neovius" },
    splittingModes = new[] { "full_wall", "full_void", "positive_half", "negative_half" },
    modulationTypes = new[] { "uniform", "z_gradient", "x_gradient", "radial", "thermal_gradient" },
    regionRequirements = new[] { "lattice", "solid", "void", "channel" },
    regionShapes = new[] { "box", "cylinder" },
    resolutions = new[] { "low", "medium", "high" }
}));

app.Run();

record TPMSRequest(string? SurfaceType, double CellSize, double WallThickness, double[]? BoundingBox, string? Resolution);
record LatticeRequest(string? UnitCell, double StrutDiameter, double[]? BoundingBox, string? Resolution);
record GenerateRequest(string? Type, string? SurfaceType, double CellSize, double WallThickness, double[]? BoundingBox, string? Resolution);
record CEMFillRequest(string? Type, string? Splitting, double? CellSizeMin, double? CellSizeMax, double? WallThicknessMin, double? WallThicknessMax, string? Modulation);
record CEMRegionRequest(string? Name, string? Requirement, double[]? Offset, double[]? Size, string? Shape, double Radius, double HeatFlux);
record CEMManufacturingRequest(string? Process, string? Material, double? MinWall, double? MaxOverhang);
record CEMRequest(string? Name, double[]? Envelope, double ShellThickness, CEMFillRequest? Fill, CEMRegionRequest[]? Regions, CEMManufacturingRequest? Manufacturing, string? Resolution);
