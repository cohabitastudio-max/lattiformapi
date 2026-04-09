// ============================================================
//  LATTIFORM API v1.0 — PicoGK Geometry Microservice
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
// ============================================================

using System.Numerics;
using System.Text.Json;
using PicoGK;
using Leap71.ShapeKernel;

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
            "https://*.lattiform.io")
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
    version = "1.0.0",
    picogk  = "headless"
});

// ── Tipos disponibles ──────────────────────────────────────
app.MapGet("/api/types", () => new
{
    tpms = new[]
    {
        new { id="gyroid",       name="Gyroid",         desc="Ideal implantes, intercambiadores de calor",  complexity="medium" },
        new { id="schwarz_p",    name="Schwarz-P",       desc="Alta porosidad uniforme, filtración, acústica", complexity="low"    },
        new { id="schwarz_d",    name="Schwarz-D/Diamond",desc="Alta resistencia estructural",               complexity="medium" },
        new { id="lidinoid",     name="Lidinoid",        desc="Alta área superficial, catalizadores",        complexity="high"   },
        new { id="neovius",      name="Neovius",         desc="Máxima área superficial",                    complexity="high"   },
        new { id="iwp",          name="IWP",             desc="Balance resistencia/flujo",                  complexity="medium" },
    },
    lattice = new[]
    {
        new { id="bcc",          name="BCC",             desc="Body-Centered Cubic, uso general",           complexity="low"  },
        new { id="fcc",          name="FCC",             desc="Face-Centered Cubic, alta densidad",         complexity="low"  },
        new { id="octet",        name="Octet Truss",     desc="Máxima resistencia/peso, aerospace",         complexity="medium"},
        new { id="kelvin",       name="Kelvin Cell",     desc="Amortiguación de impacto",                   complexity="medium"},
        new { id="diamond",      name="Diamond",         desc="Isotropía máxima",                          complexity="medium"},
    }
});

// ── Endpoint principal de generación ──────────────────────
app.MapPost("/api/generate", async (GenerateRequest req, HttpContext ctx) =>
{
    try
    {
        var result = await Task.Run(() => GenerateGeometry(req));
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title:  "Generation failed",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapPost("/api/tpms",    async (TPMSRequest req) =>
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
        "neovius"   or
        "iwp"       => BuildTPMS(req.Type, req.Params, req.BoundingBox),
        "bcc"       or
        "fcc"       or
        "octet"     or
        "kelvin"    or
        "diamond"   => BuildLattice(req.Type, req.Params, req.BoundingBox),
        _           => BuildTPMS("gyroid", req.Params, req.BoundingBox)
    };

    var mesh     = vox.mshAsMesh();
    var stlPath  = Path.Combine(Path.GetTempPath(), $"lattiform_{Guid.NewGuid():N}.stl");
    mesh.SaveToStlFile(stlPath);

    var elapsed  = (DateTime.UtcNow - startTime).TotalSeconds;
    var bytes    = new FileInfo(stlPath).Length;

    // TODO: Upload a Cloudflare R2 y devolver URL pública
    // Por ahora: base64 inline para MVP
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
            BoundingBoxMM = req.BoundingBox ?? new float[]{50,50,50},
            TriangleCount = mesh.nFaceCount,
            VertexCount   = mesh.nVertexCount,
        }
    };
}

static Voxels BuildTPMS(string type, Dictionary<string,float>? p, float[]? bbox)
{
    var bb     = bbox ?? new float[]{50,50,50};
    float cell = p?.GetValueOrDefault("cell_size",   8f) ?? 8f;
    float wall = p?.GetValueOrDefault("wall_thickness", 0.8f) ?? 0.8f;
    float iso  = p?.GetValueOrDefault("iso_value",   0f) ?? 0f;

    float X = bb[0], Y = bb[1], Z = bb[2];
    var oFr = new LocalFrame(new Vector3(X/2, Y/2, Z/2));

    IImplicit surface = type switch
    {
        "gyroid"    => new ImplicitGyroid(cell, wall),
        "schwarz_p" => new ImplicitSchwartzP(cell, wall),
        "schwarz_d" => new ImplicitDiamond(cell, wall),
        _           => new ImplicitGyroid(cell, wall)
    };

    // Bounding box como caja sólida para intersectar
    var oBoxFr = new LocalFrame(new Vector3(X/2, Y/2, Z/2));
    var voxBox = new BaseBox(oBoxFr, X, Y, Z).voxConstruct();
    voxBox.IntersectImplicit(surface);

    return voxBox;
}

static Voxels BuildLattice(string type, Dictionary<string,float>? p, float[]? bbox)
{
    var bb      = bbox ?? new float[]{50,50,50};
    float cell  = p?.GetValueOrDefault("cell_size",    10f) ?? 10f;
    float strut = p?.GetValueOrDefault("strut_radius",  0.8f) ?? 0.8f;
    float X = bb[0], Y = bb[1], Z = bb[2];

    var oLat = new Lattice();
    int nx = (int)(X / cell) + 1;
    int ny = (int)(Y / cell) + 1;
    int nz = (int)(Z / cell) + 1;

    // BCC — Body-Centered Cubic
    for (int ix = 0; ix < nx; ix++)
    for (int iy = 0; iy < ny; iy++)
    for (int iz = 0; iz < nz; iz++)
    {
        var corner = new Vector3(ix*cell, iy*cell, iz*cell);
        var center = corner + new Vector3(cell/2, cell/2, cell/2);

        if (type == "bcc" || type == "diamond")
        {
            // 8 diagonales del cubo al centro
            for (int dx = 0; dx <= 1; dx++)
            for (int dy = 0; dy <= 1; dy++)
            for (int dz = 0; dz <= 1; dz++)
            {
                var vert = corner + new Vector3(dx*cell, dy*cell, dz*cell);
                oLat.AddBeam(center, vert, strut, strut);
            }
        }
        else if (type == "octet")
        {
            // Octet: aristas + diagonales de cara
            if (ix < nx-1) oLat.AddBeam(corner,
                corner + new Vector3(cell,0,0), strut, strut);
            if (iy < ny-1) oLat.AddBeam(corner,
                corner + new Vector3(0,cell,0), strut, strut);
            if (iz < nz-1) oLat.AddBeam(corner,
                corner + new Vector3(0,0,cell), strut, strut);
            // Diagonales de cara
            if (ix < nx-1 && iy < ny-1)
                oLat.AddBeam(corner,
                    corner + new Vector3(cell,cell,0), strut*0.7f, strut*0.7f);
        }
        else // fcc, kelvin — aristas básicas
        {
            if (ix < nx-1) oLat.AddBeam(corner,
                corner + new Vector3(cell,0,0), strut, strut);
            if (iy < ny-1) oLat.AddBeam(corner,
                corner + new Vector3(0,cell,0), strut, strut);
            if (iz < nz-1) oLat.AddBeam(corner,
                corner + new Vector3(0,0,cell), strut, strut);
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
        Params      = new Dictionary<string,float>
        {
            { "cell_size",      req.CellSize      },
            { "wall_thickness", req.WallThickness  },
            { "iso_value",      req.IsoValue       }
        }
    });

static GenerateResponse GenerateLattice(LatticeRequest req)
    => GenerateGeometry(new GenerateRequest
    {
        Type        = req.LatticeType,
        Resolution  = req.Resolution,
        BoundingBox = req.BoundingBox,
        Params      = new Dictionary<string,float>
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
    public string  Type        { get; init; } = "gyroid";
    public string  Resolution  { get; init; } = "medium";
    public float[]? BoundingBox { get; init; }
    public Dictionary<string, float>? Params { get; init; }
}

record TPMSRequest
{
    public string  SurfaceType    { get; init; } = "gyroid";
    public float   CellSize       { get; init; } = 8f;
    public float   WallThickness  { get; init; } = 0.8f;
    public float   IsoValue       { get; init; } = 0f;
    public float[]? BoundingBox   { get; init; }
    public string  Resolution     { get; init; } = "medium";
}

record LatticeRequest
{
    public string  LatticeType  { get; init; } = "bcc";
    public float   CellSize     { get; init; } = 10f;
    public float   StrutRadius  { get; init; } = 0.8f;
    public float[]? BoundingBox { get; init; }
    public string  Resolution   { get; init; } = "medium";
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
