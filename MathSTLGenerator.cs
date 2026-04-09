using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

public static class MathSTLGenerator
{
    static double SchwarzP(double x, double y, double z) =>
        Math.Cos(x) + Math.Cos(y) + Math.Cos(z);

    static double Gyroid(double x, double y, double z) =>
        Math.Sin(x)*Math.Cos(y) + Math.Sin(y)*Math.Cos(z) + Math.Sin(z)*Math.Cos(x);

    static double Diamond(double x, double y, double z) =>
        Math.Sin(x)*Math.Sin(y)*Math.Sin(z) +
        Math.Sin(x)*Math.Cos(y)*Math.Cos(z) +
        Math.Cos(x)*Math.Sin(y)*Math.Cos(z) +
        Math.Cos(x)*Math.Cos(y)*Math.Sin(z);

    static double Lidinoid(double x, double y, double z) =>
        0.5*(Math.Sin(2*x)*Math.Cos(y)*Math.Sin(z) +
             Math.Sin(2*y)*Math.Cos(z)*Math.Sin(x) +
             Math.Sin(2*z)*Math.Cos(x)*Math.Sin(y)) -
        0.5*(Math.Cos(2*x)*Math.Cos(2*y) +
             Math.Cos(2*y)*Math.Cos(2*z) +
             Math.Cos(2*z)*Math.Cos(2*x)) + 0.15;

    static double SchwarzD(double x, double y, double z) =>
        Math.Cos(x)*Math.Cos(y)*Math.Cos(z) -
        Math.Sin(x)*Math.Sin(y)*Math.Sin(z);

    static double Neovius(double x, double y, double z) =>
        3*(Math.Cos(x)+Math.Cos(y)+Math.Cos(z)) +
        4*Math.Cos(x)*Math.Cos(y)*Math.Cos(z);

    public static Func<double,double,double,double> GetSurface(string type) =>
        type.ToLower() switch
        {
            "schwarz_p" or "schwarz" => SchwarzP,
            "gyroid"                 => Gyroid,
            "diamond"                => Diamond,
            "lidinoid"               => Lidinoid,
            "schwarz_d"              => SchwarzD,
            "neovius"                => Neovius,
            _                        => Gyroid
        };

    public static byte[] GenerateTPMS(
        string surfaceType, double cellSize, double wallThickness,
        double sizeX, double sizeY, double sizeZ, int gridRes)
    {
        var fn = GetSurface(surfaceType);
        double scale = 2.0 * Math.PI / cellSize;
        double isoWidth = wallThickness * scale * 0.5;

        double Field(double wx, double wy, double wz)
        {
            double v = fn(wx * scale, wy * scale, wz * scale);
            return Math.Abs(v) - isoWidth;
        }

        double dx = sizeX / gridRes, dy = sizeY / gridRes, dz = sizeZ / gridRes;
        var tris = new List<(Vector3, Vector3, Vector3)>();

        for (int ix = 0; ix < gridRes; ix++)
        for (int iy = 0; iy < gridRes; iy++)
        for (int iz = 0; iz < gridRes; iz++)
        {
            double x0 = ix*dx, x1 = x0+dx;
            double y0 = iy*dy, y1 = y0+dy;
            double z0 = iz*dz, z1 = z0+dz;

            double[] v = {
                Field(x0,y0,z0), Field(x1,y0,z0), Field(x1,y1,z0), Field(x0,y1,z0),
                Field(x0,y0,z1), Field(x1,y0,z1), Field(x1,y1,z1), Field(x0,y1,z1)
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

            int eb = MCTables.EdgeTable[ci];
            if (eb == 0) continue;

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
                tris.Add((ev[MCTables.TriTable[ci][t]],
                          ev[MCTables.TriTable[ci][t+1]],
                          ev[MCTables.TriTable[ci][t+2]]));
        }

        return WriteBinarySTL(tris);
    }

    public static byte[] GenerateLattice(
        string unitCell, double strutDiameter,
        double sizeX, double sizeY, double sizeZ, int gridRes)
    {
        double radius = strutDiameter / 2.0;

        double Field(double wx, double wy, double wz)
        {
            double fx = (wx % strutDiameter) - radius;
            double fy = (wy % strutDiameter) - radius;
            double fz = (wz % strutDiameter) - radius;
            double dxy = Math.Sqrt(fx*fx + fy*fy) - radius*0.3;
            double dyz = Math.Sqrt(fy*fy + fz*fz) - radius*0.3;
            double dxz = Math.Sqrt(fx*fx + fz*fz) - radius*0.3;
            return Math.Min(Math.Min(dxy, dyz), dxz);
        }

        double dx = sizeX/gridRes, dy = sizeY/gridRes, dz = sizeZ/gridRes;
        var tris = new List<(Vector3, Vector3, Vector3)>();

        for (int ix = 0; ix < gridRes; ix++)
        for (int iy = 0; iy < gridRes; iy++)
        for (int iz = 0; iz < gridRes; iz++)
        {
            double x0=ix*dx, x1=x0+dx, y0=iy*dy, y1=y0+dy, z0=iz*dz, z1=z0+dz;
            double[] v = {
                Field(x0,y0,z0),Field(x1,y0,z0),Field(x1,y1,z0),Field(x0,y1,z0),
                Field(x0,y0,z1),Field(x1,y0,z1),Field(x1,y1,z1),Field(x0,y1,z1)
            };
            Vector3[] c = {
                new((float)x0,(float)y0,(float)z0),new((float)x1,(float)y0,(float)z0),
                new((float)x1,(float)y1,(float)z0),new((float)x0,(float)y1,(float)z0),
                new((float)x0,(float)y0,(float)z1),new((float)x1,(float)y0,(float)z1),
                new((float)x1,(float)y1,(float)z1),new((float)x0,(float)y1,(float)z1)
            };
            int ci=0;
            for(int i=0;i<8;i++) if(v[i]<0) ci|=1<<i;
            if(ci==0||ci==255) continue;
            int eb=MCTables.EdgeTable[ci]; if(eb==0) continue;
            var ev=new Vector3[12];
            int[][] ep={new[]{0,1},new[]{1,2},new[]{2,3},new[]{3,0},
                        new[]{4,5},new[]{5,6},new[]{6,7},new[]{7,4},
                        new[]{0,4},new[]{1,5},new[]{2,6},new[]{3,7}};
            for(int e=0;e<12;e++){
                if((eb&(1<<e))==0) continue;
                int a=ep[e][0],b=ep[e][1];
                float t=(float)(v[a]/(v[a]-v[b]));
                ev[e]=Vector3.Lerp(c[a],c[b],t);
            }
            for(int t=0;MCTables.TriTable[ci][t]!=-1;t+=3)
                tris.Add((ev[MCTables.TriTable[ci][t]],
                          ev[MCTables.TriTable[ci][t+1]],
                          ev[MCTables.TriTable[ci][t+2]]));
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
            var n = Vector3.Normalize(Vector3.Cross(b-a, c-a));
            if (float.IsNaN(n.X)) n = Vector3.UnitZ;
            bw.Write(n.X);bw.Write(n.Y);bw.Write(n.Z);
            bw.Write(a.X);bw.Write(a.Y);bw.Write(a.Z);
            bw.Write(b.X);bw.Write(b.Y);bw.Write(b.Z);
            bw.Write(c.X);bw.Write(c.Y);bw.Write(c.Z);
            bw.Write((ushort)0);
        }
        return ms.ToArray();
    }
}
