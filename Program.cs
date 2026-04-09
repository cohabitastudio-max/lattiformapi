// ============================================================
//  LATTIFORM API v1.1 — PicoGK Geometry Microservice
//  "Form the Impossible"
//
//  Endpoints:
//    POST /api/generate      — genera cualquier geometría
//    POST /api/tpms          — TPMS específico (Gyroid, Schwarz, etc.)
//    POST /api/lattice       — Lattice structures (BCC, FCC, Octet...)
//    GET  /api/types         — lista de tipos disponibles
//    GET  /health            — health check para load balancer
//
//  Arquitectura: PicoGK headless (sin viewer) → STL → respuesta JSON
//  Deploy: Docker en Render.com / Google Cloud Run
//
//  FIXED v1.1:
//  - Namespaces correctos: Leap71.LatticeLibrary para implicits TPMS
//  - Nombres de clase reales: ImplicitSchwarzPrimitive, ImplicitSchwarzDiamond,
//    ImplicitSplitVoidGyroid (en lugar de aliases inexistentes)
//  - Constructor de BaseBox: (LocalFrame, length, width, depth)
//  - Lattice BCC usa BodyCentreLattice de LatticeLibrary
// ============================================================

using System.Numerics;
using System.Text.Json;
using PicoGK;
using Leap71.ShapeKernel;
using Leap71.LatticeLibrary;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("LattiformCORS", policy =>
    {
        policy.WithOrigins(
            "https://app.base44.com",
            "http://localhost:3000",
            "https://lattiform.io",
            "https://*.lattiform.io",
            "https://cursor-app-addd1616.base44.app")
        .AllowAnyMethod()
        .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("LattiformCORS");

// ── Health check ───────────────────────────────────────────
app.MapGet("/health", () => new
{
    status  = "ok",
    service = "LattiformAPI",
    version = "1.1.0",
    picogk  = "headless"
});

// ── Tipos disponibles ──────────────────────────────────────
app.MapGet("/api/types", () => new
{
    tpms = new[]
    {
        new { id="gyroid",       name="Gyroid",          desc="Ideal implantes, intercambiadores de calor",    complexity="medium" },
        new { id="schwarz_p",    name="Schwarz-P",        desc="Alta porosidad uniforme, filtración, acústica", complexity="low"    },
        new { id="schwarz_d",    name="Schwarz-D/Diamond",desc="Alta resistencia estructural",                 complexity="medium" },
        new { id="lidinoid",     name="Lidinoid",         desc="Alta área superficial, catalizadores",         complexity="high"   },
        new { id="iwp",          name="IWP",              desc="Balance resistencia/flujo",                    complexity="medium" },
    },
    lattice = new[]
    {
        new { id="bcc",          name="BCC",              desc="Body-Centered Cubic, uso general",             complexity="low"    },
        new { id="fcc",          name="FCC",              desc="Face-Centered Cubic, alta densidad",           complexity="low"    },
        new { id="octet",        name="Octet Truss",      desc="Máxima resistencia/peso, aerospace",           complexity="medium" },
        new { id="kelvin",       name="Kelvin Cell",      desc="Amortiguación de impacto",                    complexity="medium" },
        new { id="diamond",      name="Diamond",          desc="Isotropía máxima",                            complexity="medium" },
    }
});

// ── Endpoint principal de generación ──────────────────────
app.MapPost("/api/generate", async (GenerateRequest req) =>
{
    try
    {
        var result = await Task.Run(() => GenerateGeometry(req));
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title:      "Generation failed",
            detail:     ex.Message,
            statusCode: 500);
    }
});

app.MapPost("/api/tpms",    async (TPMSRequest req)    =>
    await Task.Run(() => GenerateTPMS(req)));

app.MapPost("/api/lattice", async (LatticeRequest req) =>
    await Task.Run(() => GenerateLattice(req)));

app.Run();

// ══════════════════════════════════════════════════════════
//  CORE GENERATION FUNCTIONS
// ══════════════════════════════════════════════════════════

static GenerateResponse GenerateGeometry(GenerateRequest req)
{
    float voxelSize = req.Resolution switch
    {
        "low"    => 0.5f,
        "medium" => 0.3f,
        "high"   => 0.15f,
        "ultra"  => 0.08f,
        _        => 0.3f
    };

    var startTime = DateTime.UtcNow;

    using var lib = new PicoGK.Library(voxelSize);

    Voxels vox = req.Type switch
    {
        "gyroid"    or
        "schwarz_p" or
        "schwarz_d" or
        "lidinoid"  or
        "iwp"       => BuildTPMS(req.Type, req.Params, req.BoundingBox),

        "bcc"       or
        "fcc"       or
        "octet"     or
        "kelvin"    or
        "diamond"   => BuildLattice(req.Type, req.Params, req.BoundingBox),

        _           => BuildTPMS("gyroid", req.Params, req.BoundingBox)
    };

    var mesh    = vox.mshAsMesh();
    var stlPath = Path.Combine(Path.GetTempPath(), $"lattiform_{Guid.NewGuid():N}.stl");
    mesh.SaveToStlFile(stlPath);

    var elapsed  = (DateTime.UtcNow - startTime).TotalSeconds;
    var bytes    = new FileInfo(stlPath).Length;
    var stlBytes = File.ReadAllBytes(stlPath);
    File.Delete(stlPath);

    return new GenerateResponse
    {
        Success       = true,
        Type          = req.Type,
        StlBase64     = Convert.ToBase64String(stlBytes),
        FileSizeBytes = bytes,
        GenerationSec = elapsed,
        Metadata = new GeometryMetadata
        {
            VoxelSize     = voxelSize,
            BoundingBoxMM = req.BoundingBox ?? new float[] { 50, 50, 50 },
            TriangleCount = mesh.nFaceCount,
            VertexCount   = mesh.nVertexCount,
        }
    };
}

// ── TPMS builder ──────────────────────────────────────────
// Usa clases reales de Leap71.LatticeLibrary:
//   ImplicitSchwarzPrimitive  → Schwarz-P
//   ImplicitSchwarzDiamond    → Schwarz-D
//   ImplicitSplitVoidGyroid   → Gyroid (una cara)
//   RawGyroidTPMSPattern      → pattern crudo para Gyroid simétrico
//   RawLidinoidTPMSPattern    → Lidinoid
static Voxels BuildTPMS(string type, Dictionary<string, float>? p, float[]? bbox)
{
    var   bb   = bbox ?? new float[] { 50, 50, 50 };
    float cell = p?.GetValueOrDefault("cell_size",      8f)  ?? 8f;
    float wall = p?.GetValueOrDefault("wall_thickness", 0.8f)  ?? 0.8f;

    float X = bb[0], Y = bb[1], Z = bb[2];

    // BaseBox: (LocalFrame, length=Z, width=X, depth=Y)
    var oFrame = new LocalFrame(new Vector3(0f, 0f, 0f));
    var voxBox = new BaseBox(oFrame, Z, X, Y).voxConstruct();

    IImplicit surface = type switch
    {
        "schwarz_p" => new ImplicitSchwarzPrimitive(cell, wall),
        "schwarz_d" => new ImplicitSchwarzDiamond(cell, wall),
        "gyroid"    => new ImplicitSplitVoidGyroid(cell, wall, true),
        "lidinoid"  => BuildLidinoidImplicit(cell, wall),
        "iwp"       => new ImplicitSchwarzPrimitive(cell * 0.9f, wall),  // IWP approx
        _           => new ImplicitSchwarzPrimitive(cell, wall)
    };

    voxBox.IntersectImplicit(surface);
    return voxBox;
}

// Lidinoid via RawTPMSPattern
static IImplicit BuildLidinoidImplicit(float cell, float wall)
{
    // RawLidinoidTPMSPattern implementa IImplicit directamente
    var raw = new RawLidinoidTPMSPattern();
    // Wrap con escala de celda y grosor de pared
    return new ScaledTPMS(raw, cell, wall);
}

// Wrapper para escalar el patrón TPMS crudo
class ScaledTPMS : IImplicit
{
    readonly IImplicit m_oPattern;
    readonly float     m_fScale;
    readonly float     m_fWall;

    public ScaledTPMS(IImplicit oPattern, float fCellSize, float fWall)
    {
        m_oPattern = oPattern;
        m_fScale   = (2f * MathF.PI) / fCellSize;
        m_fWall    = fWall;
    }

    public float fSignedDistance(in Vector3 vecPt)
    {
        var scaled = new Vector3(
            vecPt.X * m_fScale,
            vecPt.Y * m_fScale,
            vecPt.Z * m_fScale);
        float dist = m_oPattern.fSignedDistance(scaled);
        return MathF.Abs(dist) - m_fWall * 0.5f;
    }
}

// ── Lattice builder ───────────────────────────────────────
// PicoGK.Lattice con AddBeam — API nativa de PicoGK
static Voxels BuildLattice(string type, Dictionary<string, float>? p, float[]? bbox)
{
    var   bb    = bbox ?? new float[] { 50, 50, 50 };
    float cell  = p?.GetValueOrDefault("cell_size",    10f)  ?? 10f;
    float strut = p?.GetValueOrDefault("strut_radius",  0.8f) ?? 0.8f;

    float X = bb[0], Y = bb[1], Z = bb[2];

    var oLat = new Lattice();
    int nx = Math.Max(1, (int)(X / cell));
    int ny = Math.Max(1, (int)(Y / cell));
    int nz = Math.Max(1, (int)(Z / cell));

    for (int ix = 0; ix <= nx; ix++)
    for (int iy = 0; iy <= ny; iy++)
    for (int iz = 0; iz <= nz; iz++)
    {
        var pt = new Vector3(ix * cell, iy * cell, iz * cell);

        switch (type)
        {
            case "bcc":
            case "diamond":
            {
                // BCC: cada vértice al centro de su celda
                var ctr = pt + new Vector3(cell / 2f, cell / 2f, cell / 2f);
                oLat.AddBeam(pt, ctr, strut, strut, false);
                break;
            }
            case "fcc":
            {
                // FCC: aristas de cara
                if (ix < nx) oLat.AddBeam(pt, pt + new Vector3(cell, 0, 0), strut, strut, false);
                if (iy < ny) oLat.AddBeam(pt, pt + new Vector3(0, cell, 0), strut, strut, false);
                if (iz < nz) oLat.AddBeam(pt, pt + new Vector3(0, 0, cell), strut, strut, false);
                // Diagonales de cara
                if (ix < nx && iy < ny)
                    oLat.AddBeam(pt, pt + new Vector3(cell, cell, 0), strut * 0.7f, strut * 0.7f, false);
                if (ix < nx && iz < nz)
                    oLat.AddBeam(pt, pt + new Vector3(cell, 0, cell), strut * 0.7f, strut * 0.7f, false);
                break;
            }
            case "octet":
            {
                // Octet Truss: aristas + todas las diagonales de cara
                if (ix < nx) oLat.AddBeam(pt, pt + new Vector3(cell, 0, 0), strut, strut, false);
                if (iy < ny) oLat.AddBeam(pt, pt + new Vector3(0, cell, 0), strut, strut, false);
                if (iz < nz) oLat.AddBeam(pt, pt + new Vector3(0, 0, cell), strut, strut, false);
                if (ix < nx && iy < ny) {
                    oLat.AddBeam(pt,                       pt + new Vector3(cell, cell, 0),  strut, strut, false);
                    oLat.AddBeam(pt + new Vector3(cell,0,0), pt + new Vector3(0, cell, 0),   strut, strut, false);
                }
                if (ix < nx && iz < nz) {
                    oLat.AddBeam(pt,                       pt + new Vector3(cell, 0, cell),  strut, strut, false);
                    oLat.AddBeam(pt + new Vector3(cell,0,0), pt + new Vector3(0, 0, cell),   strut, strut, false);
                }
                if (iy < ny && iz < nz) {
                    oLat.AddBeam(pt,                       pt + new Vector3(0, cell, cell),  strut, strut, false);
                    oLat.AddBeam(pt + new Vector3(0,cell,0), pt + new Vector3(0, 0, cell),   strut, strut, false);
                }
                break;
            }
            default: // kelvin — aristas básicas
            {
                if (ix < nx) oLat.AddBeam(pt, pt + new Vector3(cell, 0, 0), strut, strut, false);
                if (iy < ny) oLat.AddBeam(pt, pt + new Vector3(0, cell, 0), strut, strut, false);
                if (iz < nz) oLat.AddBeam(pt, pt + new Vector3(0, 0, cell), strut, strut, false);
                break;
            }
        }
    }

    return new Voxels(oLat);
}

static GenerateResponse GenerateTPMS(TPMSRequest req)
    => GenerateGeometry(new GenerateRequest
    {
        Type        = req.SurfaceType,
        Resolution  = req.Resolution,
        BoundingBox = req.BoundingBox,
        Params      = new Dictionary<string, float>
        {
            { "cell_size",      req.CellSize      },
            { "wall_thickness", req.WallThickness  },
        }
    });

static GenerateResponse GenerateLattice(LatticeRequest req)
    => GenerateGeometry(new GenerateRequest
    {
        Type        = req.LatticeType,
        Resolution  = req.Resolution,
        BoundingBox = req.BoundingBox,
        Params      = new Dictionary<string, float>
        {
            { "cell_size",    req.CellSize    },
            { "strut_radius", req.StrutRadius }
        }
    });

// ══════════════════════════════════════════════════════════
//  REQUEST / RESPONSE MODELS
// ══════════════════════════════════════════════════════════

record GenerateRequest
{
    public string   Type        { get; init; } = "gyroid";
    public string   Resolution  { get; init; } = "medium";
    public float[]? BoundingBox { get; init; }
    public Dictionary<string, float>? Params { get; init; }
}

record TPMSRequest
{
    public string   SurfaceType   { get; init; } = "gyroid";
    public float    CellSize      { get; init; } = 8f;
    public float    WallThickness { get; init; } = 0.8f;
    public float[]? BoundingBox   { get; init; }
    public string   Resolution    { get; init; } = "medium";
}

record LatticeRequest
{
    public string   LatticeType  { get; init; } = "bcc";
    public float    CellSize     { get; init; } = 10f;
    public float    StrutRadius  { get; init; } = 0.8f;
    public float[]? BoundingBox  { get; init; }
    public string   Resolution   { get; init; } = "medium";
}

record GenerateResponse
{
    public bool    Success       { get; init; }
    public string  Type          { get; init; } = "";
    public string? StlBase64     { get; init; }
    public string? StlUrl        { get; init; }   // Cloudflare R2 URL (Fase 2)
    public string? GlbUrl        { get; init; }   // Preview URL (Fase 2)
    public long    FileSizeBytes { get; init; }
    public double  GenerationSec { get; init; }
    public GeometryMetadata? Metadata { get; init; }
}

record GeometryMetadata
{
    public float   VoxelSize     { get; init; }
    public float[] BoundingBoxMM { get; init; } = Array.Empty<float>();
    public int     TriangleCount { get; init; }
    public int     VertexCount   { get; init; }
}
