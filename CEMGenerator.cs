using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

public static class CEMGenerator
{
    public delegate double ScalarField(double x, double y, double z);
    public delegate double SDFField(double x, double y, double z);

    public static ScalarField Uniform(double value) => (x, y, z) => value;

    public static ScalarField LinearGradient(int axis, double min, double max, double size) =>
        (x, y, z) => { double t = Math.Clamp((axis switch { 0 => x, 1 => y, _ => z }) / size, 0, 1); return min + t * (max - min); };

    public static ScalarField RadialGradient(double cx, double cy, double cz, double radius, double center, double edge) =>
        (x, y, z) => { double r = Math.Sqrt((x-cx)*(x-cx)+(y-cy)*(y-cy)+(z-cz)*(z-cz)); return center + Math.Clamp(r/radius,0,1) * (edge-center); };

    public static ScalarField HotSpot(double cx, double cy, double cz, double radius, double peak, double baseline) =>
        (x, y, z) => { double r2 = (x-cx)*(x-cx)+(y-cy)*(y-cy)+(z-cz)*(z-cz); double s2 = radius*radius/2.3; return baseline + (peak-baseline)*Math.Exp(-r2/(2*s2)); };

    public static ScalarField CombineFields(ScalarField a, ScalarField b) =>
        (x, y, z) => Math.Max(a(x,y,z), b(x,y,z));

    static double Gyroid(double x, double y, double z) =>
        Math.Sin(x)*Math.Cos(y) + Math.Sin(y)*Math.Cos(z) + Math.Sin(z)*Math.Cos(x);
    static double SchwarzP(double x, double y, double z) =>
        Math.Cos(x) + Math.Cos(y) + Math.Cos(z);
    static double SchwarzD(double x, double y, double z) =>
        Math.Cos(x)*Math.Cos(y)*Math.Cos(z) - Math.Sin(x)*Math.Sin(y)*Math.Sin(z);
    static double DiamondFn(double x, double y, double z) =>
        Math.Sin(x)*Math.Sin(y)*Math.Sin(z) + Math.Sin(x)*Math.Cos(y)*Math.Cos(z) +
        Math.Cos(x)*Math.Sin(y)*Math.Cos(z) + Math.Cos(x)*Math.Cos(y)*Math.Sin(z);
    static double Lidinoid(double x, double y, double z) =>
        0.5*(Math.Sin(2*x)*Math.Cos(y)*Math.Sin(z) + Math.Sin(2*y)*Math.Cos(z)*Math.Sin(x) +
        Math.Sin(2*z)*Math.Cos(x)*Math.Sin(y)) - 0.5*(Math.Cos(2*x)*Math.Cos(2*y) +
        Math.Cos(2*y)*Math.Cos(2*z) + Math.Cos(2*z)*Math.Cos(2*x)) + 0.15;
    static double Neovius(double x, double y, double z) =>
        3*(Math.Cos(x)+Math.Cos(y)+Math.Cos(z)) + 4*Math.Cos(x)*Math.Cos(y)*Math.Cos(z);

    public static Func<double,double,double,double> GetSurface(string type) =>
        type.ToLower() switch {
            "schwarz_p" => SchwarzP, "gyroid" => Gyroid, "diamond" => DiamondFn,
            "lidinoid" => Lidinoid, "schwarz_d" => SchwarzD, "neovius" => Neovius, _ => Gyroid };

    public class CEMRegion
    {
        public string Name { get; set; } = "";
        public string Requirement { get; set; } = "lattice";
        public double[]? Offset { get; set; }
        public double[]? Size { get; set; }
        public string Shape { get; set; } = "box";
        public double Radius { get; set; }
        public double HeatFlux { get; set; }
    }

    public class CEMFill
    {
        public string Type { get; set; } = "gyroid";
        public string Splitting { get; set; } = "full_wall";
        public double CellSizeMin { get; set; } = 5;
        public double CellSizeMax { get; set; } = 5;
        public double WallThicknessMin { get; set; } = 0.8;
        public double WallThicknessMax { get; set; } = 0.8;
        public string Modulation { get; set; } = "uniform";
    }

    public class CEMManufacturing
    {
        public string Process { get; set; } = "SLS";
        public string Material { get; set; } = "PA12";
        public double MinWall { get; set; } = 0.6;
        public double MaxOverhang { get; set; } = 45;
    }

    public class CEMModel
    {
        public string Name { get; set; } = "Part";
        public double[] Envelope { get; set; } = new[] { 40.0, 40.0, 40.0 };
        public double ShellThickness { get; set; }
        public CEMRegion[]? Regions { get; set; }
        public CEMFill Fill { get; set; } = new();
        public CEMManufacturing Manufacturing { get; set; } = new();
        public string Resolution { get; set; } = "low";
    }

    public static byte[] Generate(CEMModel model)
    {
        double sx = model.Envelope[0], sy = model.Envelope[1], sz = model.Envelope[2];
        int gridRes = model.Resolution switch { "low" => 60, "medium" => 100, "high" => 160, _ => 80 };

        var tpmsFn = GetSurface(model.Fill.Type);

        bool isVariable = model.Fill.Modulation != "uniform" &&
            (model.Fill.CellSizeMin != model.Fill.CellSizeMax ||
             model.Fill.WallThicknessMin != model.Fill.WallThicknessMax);

        ScalarField cellSizeField;
        ScalarField wallField;

        if (!isVariable)
        {
            cellSizeField = Uniform(model.Fill.CellSizeMin);
            wallField = Uniform(model.Fill.WallThicknessMin);
        }
        else
        {
            ScalarField baseMod = model.Fill.Modulation switch
            {
                "z_gradient" => LinearGradient(2, 0, 1, sz),
                "x_gradient" => LinearGradient(0, 0, 1, sx),
                "radial" => RadialGradient(sx*0.5, sy*0.5, sz*0.5, Math.Max(sx, Math.Max(sy, sz))*0.5, 0, 1),
                "thermal_gradient" => BuildThermalField(model, sx, sy, sz),
                _ => Uniform(0.5)
            };
            cellSizeField = (x, y, z) => {
                double t = Math.Clamp(baseMod(x,y,z), 0, 1);
                return model.Fill.CellSizeMin + t * (model.Fill.CellSizeMax - model.Fill.CellSizeMin);
            };
            wallField = (x, y, z) => {
                double t = Math.Clamp(baseMod(x,y,z), 0, 1);
                return model.Fill.WallThicknessMin + t * (model.Fill.WallThicknessMax - model.Fill.WallThicknessMin);
            };
        }

        // TPMS field: negative = inside material
        // Convention: marching cubes extracts surface where field < 0
        SDFField tpmsField = (x, y, z) =>
        {
            double cs = cellSizeField(x, y, z);
            double wt = wallField(x, y, z);
            double scale = 2.0 * Math.PI / cs;
            double raw = tpmsFn(x * scale, y * scale, z * scale);
            double isoWidth = wt * scale * 0.5;

            double dist = model.Fill.Splitting switch
            {
                "full_void" => -(Math.Abs(raw) - isoWidth),
                "positive_half" => Math.Max(raw, Math.Abs(raw) - isoWidth),
                "negative_half" => Math.Max(-raw, Math.Abs(raw) - isoWidth),
                _ => Math.Abs(raw) - isoWidth
            };
            return dist;
        };

        // Envelope: negative inside the box
        SDFField envelope = (x, y, z) =>
        {
            double dx = Math.Max(0 - x, x - sx);
            double dy = Math.Max(0 - y, y - sy);
            double dz = Math.Max(0 - z, z - sz);
            return Math.Max(dx, Math.Max(dy, dz));
        };

        // Combined: inside when BOTH tpms < 0 AND envelope < 0
        SDFField combined = (x, y, z) => Math.Max(tpmsField(x,y,z), envelope(x,y,z));

        // Process regions
        if (model.Regions != null)
        {
            foreach (var region in model.Regions)
            {
                double ox = region.Offset != null && region.Offset.Length > 0 ? region.Offset[0] : 0;
                double oy = region.Offset != null && region.Offset.Length > 1 ? region.Offset[1] : 0;
                double oz = region.Offset != null && region.Offset.Length > 2 ? region.Offset[2] : 0;

                if (region.Requirement == "solid")
                {
                    SDFField solidRegion;
                    if (region.Shape == "cylinder" && region.Radius > 0)
                    {
                        double cr = region.Radius;
                        solidRegion = (x, y, z) =>
                        {
                            double ddx = x - ox, ddy = y - oy;
                            double rDist = Math.Sqrt(ddx*ddx + ddy*ddy) - cr;
                            return Math.Max(rDist, envelope(x,y,z));
                        };
                    }
                    else
                    {
                        double rsx = region.Size != null && region.Size.Length > 0 ? region.Size[0] : 5;
                        double rsy = region.Size != null && region.Size.Length > 1 ? region.Size[1] : 5;
                        double rsz = region.Size != null && region.Size.Length > 2 ? region.Size[2] : sz;
                        solidRegion = (x, y, z) => Math.Max(
                            Math.Max(Math.Max(ox - x, x - (ox + rsx)), Math.Max(oy - y, y - (oy + rsy))),
                            Math.Max(oz - z, z - (oz + rsz)));
                    }
                    // Union: inside if EITHER combined < 0 OR solidRegion < 0
                    var prev = combined;
                    combined = (x, y, z) => Math.Min(prev(x,y,z), solidRegion(x,y,z));
                }
                else if (region.Requirement == "void" || region.Requirement == "channel")
                {
                    SDFField holeRegion;
                    if (region.Shape == "cylinder" && region.Radius > 0)
                    {
                        double cr = region.Radius;
                        holeRegion = (x, y, z) =>
                        {
                            double ddx = x - ox, ddy = y - oy;
                            return Math.Sqrt(ddx*ddx + ddy*ddy) - cr;
                        };
                    }
                    else
                    {
                        double rsx = region.Size != null && region.Size.Length > 0 ? region.Size[0] : 5;
                        double rsy = region.Size != null && region.Size.Length > 1 ? region.Size[1] : 5;
                        double rsz = region.Size != null && region.Size.Length > 2 ? region.Size[2] : sz;
                        holeRegion = (x, y, z) => Math.Max(
                            Math.Max(Math.Max(ox - x, x - (ox + rsx)), Math.Max(oy - y, y - (oy + rsy))),
                            Math.Max(oz - z, z - (oz + rsz)));
                    }
                    // Subtract: inside combined BUT outside hole
                    var prev = combined;
                    combined = (x, y, z) => Math.Max(prev(x,y,z), -holeRegion(x,y,z));
                }
            }
        }

        // Shell
        if (model.ShellThickness > 0)
        {
            double st = model.ShellThickness;
            SDFField shell = (x, y, z) => Math.Abs(envelope(x,y,z)) - st;
            var prev = combined;
            combined = (x, y, z) => Math.Min(prev(x,y,z), shell(x,y,z));
        }

        return MarchField(combined, sx, sy, sz, gridRes);
    }

    static ScalarField BuildThermalField(CEMModel model, double sx, double sy, double sz)
    {
        ScalarField field = Uniform(0);
        if (model.Regions == null) return field;
        foreach (var r in model.Regions)
        {
            if (r.HeatFlux <= 0) continue;
            double ox = r.Offset != null && r.Offset.Length > 0 ? r.Offset[0] : sx * 0.5;
            double oy = r.Offset != null && r.Offset.Length > 1 ? r.Offset[1] : sy * 0.5;
            double oz = r.Offset != null && r.Offset.Length > 2 ? r.Offset[2] : sz * 0.5;
            double radius = Math.Max(sx, Math.Max(sy, sz)) * 0.6;
            ScalarField spot = HotSpot(ox, oy, oz, radius, 1.0, 0);
            field = CombineFields(field, spot);
        }
        return field;
    }

    static byte[] MarchField(SDFField field, double sx, double sy, double sz, int gridRes)
    {
        double dx = sx / gridRes, dy = sy / gridRes, dz = sz / gridRes;
        var tris = new List<(Vector3, Vector3, Vector3)>();

        for (int ix = 0; ix < gridRes; ix++)
        for (int iy = 0; iy < gridRes; iy++)
        for (int iz = 0; iz < gridRes; iz++)
        {
            double x0 = ix*dx, x1 = x0+dx, y0 = iy*dy, y1 = y0+dy, z0 = iz*dz, z1 = z0+dz;
            double[] v = {
                field(x0,y0,z0), field(x1,y0,z0), field(x1,y1,z0), field(x0,y1,z0),
                field(x0,y0,z1), field(x1,y0,z1), field(x1,y1,z1), field(x0,y1,z1)
            };
            Vector3[] c = {
                new((float)x0,(float)y0,(float)z0), new((float)x1,(float)y0,(float)z0),
                new((float)x1,(float)y1,(float)z0), new((float)x0,(float)y1,(float)z0),
                new((float)x0,(float)y0,(float)z1), new((float)x1,(float)y0,(float)z1),
                new((float)x1,(float)y1,(float)z1), new((float)x0,(float)y1,(float)z1)
            };
            int ci = 0;
            for (int i = 0; i < 8; i++) if (v[i] < 0) ci |= 1 << i;
            if (ci == 0 || ci == 255) continue;
            int eb = MCTables.EdgeTable[ci]; if (eb == 0) continue;
            var ev = new Vector3[12];
            int[][] ep = {
                new[]{0,1},new[]{1,2},new[]{2,3},new[]{3,0},
                new[]{4,5},new[]{5,6},new[]{6,7},new[]{7,4},
                new[]{0,4},new[]{1,5},new[]{2,6},new[]{3,7}
            };
            for (int e = 0; e < 12; e++)
            {
                if ((eb & (1 << e)) == 0) continue;
                int a = ep[e][0], b = ep[e][1];
                float t = (float)(v[a] / (v[a] - v[b]));
                ev[e] = Vector3.Lerp(c[a], c[b], t);
            }
            for (int t = 0; MCTables.TriTable[ci][t] != -1; t += 3)
                tris.Add((ev[MCTables.TriTable[ci][t]], ev[MCTables.TriTable[ci][t+1]], ev[MCTables.TriTable[ci][t+2]]));
        }
        return WriteBinarySTL(tris);
    }

    static byte[] WriteBinarySTL(List<(Vector3 a, Vector3 b, Vector3 c)> tris)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(new byte[80]);
        bw.Write((uint)tris.Count);
        foreach (var (a, b, c) in tris)
        {
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (float.IsNaN(n.X)) n = Vector3.UnitZ;
            bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z);
            bw.Write(a.X); bw.Write(a.Y); bw.Write(a.Z);
            bw.Write(b.X); bw.Write(b.Y); bw.Write(b.Z);
            bw.Write(c.X); bw.Write(c.Y); bw.Write(c.Z);
            bw.Write((ushort)0);
        }
        return ms.ToArray();
    }
}
