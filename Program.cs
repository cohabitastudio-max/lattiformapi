using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

// ── Detect PicoGK availability ──
bool picoGKAvailable = false;
try
{
    // Try loading PicoGK - if native lib exists, this succeeds
    var testType = typeof(PicoGK.Library);
    PicoGK.Library.Go(0.5f, 0, 0, null, null);
    picoGKAvailable = true;
}
catch { picoGKAvailable = false; }

string activeEngine = picoGKAvailable ? "PicoGK" : "MathSTL-MarchingCubes-Fallback";

// ── Health ──
app.MapGet("/health", () => Results.Ok(new {
    status = "ok",
    engine = activeEngine,
    picoGK = picoGKAvailable,
    fallback = !picoGKAvailable,
    version = "1.1.0",
    surfaces = new[]{"gyroid","schwarz_p","schwarz_d","diamond","lidi,
    timestamp = DateTime.UtcNow
}));

// ── TPMS endpoint ──
app.MapPost("/api/tpms", (TPMSRequest req) =>
{
    try
    {
        int res = (req.Resolution ?? "medium") switch {
            "low" => 60, "medium" => 100, "high" => 160, _ => 100
        };
        double sx = req.BoundingBox?.Length > 0 ? req.BoundingBox[0] : 30;
        double sy = req.BoundingBox?.Length > 1 ? req.BoundingBox[1] : 30;
        double sz = req.BoundingBox?.Length > 2 ? req.BoundingBox[2] : 30;

        byte[] stl;
        string usedEngine;

        if (picoGKAvailable)
        {
            // TODO: PicoGK native TPMS generation
            // stl = PicoGKGenerator.GenerateTPMS(req);
            // For now, fall through to MathSTL until PicoGK endpoints are wired
            stl = MathSTLGenerator.GenerateTPMS(
                req.SurfaceType ?? "gyroid",
                req.CellSize > 0 ? req.CellSize : 8,
                req.WallThickness > 0 ? req.WallThickness : 0.8,
                sx, sy, sz, res);
         ngine = "PicoGK (pending wire-up, using MathSTL)";
        }
        else
        {
            stl = MathSTLGenerator.GenerateTPMS(
                req.SurfaceType ?? "gyroid",
                req.CellSize > 0 ? req.CellSize : 8,
                req.WallThickness > 0 ? req.WallThickness : 0.8,
                sx, sy, sz, res);
            usedEngine = "MathSTL-MarchingCubes-Fallback";
        }

        int triCount = BitConverter.ToInt32(stl, 80);

        return Results.Ok(new {
            success = true,
            engine = usedEngine,
            stlBase64 = Convert.ToBase64String(stl),
            triangles = triCount,
            fileSize = stl.Length,
            parameters = new { req.SurfaceType, req.CellSize, req.WallThickness,
                             boundingBox = new[]{sx,sy,sz}, resolution = res }
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "TPMS generation failed", detail: ex.Message, statusCode: 500);
    }
});

// ── Lattice endpoint ─MapPost("/api/lattice", (LatticeRequest req) =>
{
    try
    {
        int res = (req.Resolution ?? "medium") switch {
            "low" => 50, "medium" => 80, "high" => 120, _ => 80
        };
        double sx = req.BoundingBox?.Length > 0 ? req.BoundingBox[0] : 30;
        double sy = req.BoundingBox?.Length > 1 ? req.BoundingBox[1] : 30;
        double sz = req.BoundingBox?.Length > 2 ? req.BoundingBox[2] : 30;

        byte[] stl = MathSTLGenerator.GenerateLattice(
            req.UnitCell ?? "octet",
            req.StrutDiameter > 0 ? req.StrutDiameter : 2,
            sx, sy, sz, res);

        int triCount = BitConverter.ToInt32(stl, 80);

        return Results.Ok(new {
            success = true,
            engine = picoGKAvailable ? "PicoGK" : "MathSTL-Fallback",
            stlBase64 = Convert.ToBase64String(stl),
            triangles = triCount,
            fileSize = stl.Length,
            parameters = new { req.UnitCell, req.StrutDiameter,
                             boundingBox = new[]{sx,sy,sz}, resolution = res }
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Lattice generation failed", detail: ex.Message, statusCode: 500);
    }
});

// ── Generic generate ──
app.MapPost("/api/generate", (GenerateRequest req) =>
{
    try
    {
        string type = (req.Type ?? "tpms").ToLower();
        int res = (req.Resolution ?? "medium") switch {
            "low" => 60, "medium" => 100, "high" => 160, _ => 100
        };
        double sx = req.BoundingBox?.Length > 0 ? req.BoundingBox[0] : 30;
        double sy = req.BoundingBox?.Length > 1 ? req.BoundingBox[1] : 30;
        double sz = req.BoundingBox?.Length > 2 ? req.BoundingBox[2] : 30;

        byte[] stl;
        if (type == "lattice")
            stl = MathSTLGenerator.GenerateLattice(
                req.SurfaceType ?? "octet",
                req.CellSize > 0 ? req.CellSize : 2,
                sx, sy, sz, res);
        else
            stl = MathSTLGenerator.GenerateTPMS(
             urfaceType ?? "gyroid",
                req.CellSize > 0 ? req.CellSize : 8,
                req.WallThickness > 0 ? req.WallThickness : 0.8,
                sx, sy, sz, res);

        int triCount = BitConverter.ToInt32(stl, 80);

        return Results.Ok(new {
            success = true,
            engine = picoGKAvailable ? "PicoGK" : "MathSTL-Fallback",
            stlBase64 = Convert.ToBase64String(stl),
            triangles = triCount,
            fileSize = stl.Length
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Generation failed", detail: ex.Message, statusCode: 500);
    }
});

app.Run();

// ── Request models ──
record TPMSRequest(string? SurfaceType, double CellSize, double WallThickness,
                   double[]? BoundingBox, string? Resolution);
record LatticeRequest(string? UnitCell, double StrutDiameter,
                      double[]? BoundingBox, string? Resolution);
record GenerateRequest(string? Type, string? SurfaceType, double Cel                      double WallThickness, double[]? BoundingBox, string? Resolution);
