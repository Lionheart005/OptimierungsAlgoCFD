using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace MyPicoGkProject
{
    public static class StlSmoother
    {
        private class VertexMerger
        {
            private readonly float _epsilonSq;
            private readonly Dictionary<(int, int, int), List<int>> _grid;
            public readonly List<Vector3> Vertices;
            private readonly float _epsilon;

            public VertexMerger(float epsilon = 1e-2f)
            {
                _epsilon = epsilon;
                _epsilonSq = epsilon * epsilon;
                _grid = new Dictionary<(int, int, int), List<int>>();
                Vertices = new List<Vector3>();
            }

            public int AddVertex(Vector3 v)
            {
                int cx = (int)Math.Floor(v.X / _epsilon);
                int cy = (int)Math.Floor(v.Y / _epsilon);
                int cz = (int)Math.Floor(v.Z / _epsilon);

                for (int i = -1; i <= 1; i++)
                {
                    for (int j = -1; j <= 1; j++)
                    {
                        for (int k = -1; k <= 1; k++)
                        {
                            var cell = (cx + i, cy + j, cz + k);
                            if (_grid.TryGetValue(cell, out var points) && points != null)
                            {
                                foreach (int pIndex in points)
                                {
                                    if (Vector3.DistanceSquared(Vertices[pIndex], v) <= _epsilonSq)
                                    {
                                        return pIndex;
                                    }
                                }
                            }
                        }
                    }
                }

                int newIndex = Vertices.Count;
                Vertices.Add(v);
                var centerCell = (cx, cy, cz);
                
                if (!_grid.TryGetValue(centerCell, out var cellList) || cellList == null)
                {
                    cellList = new List<int>();
                    _grid[centerCell] = cellList;
                }
                cellList.Add(newIndex);
                return newIndex;
            }
        }

        public static void SmoothStl(string inputPath, string outputPath, int iterations, int preMeltingSteps)
        {
            if (iterations <= 0)
            {
                if (inputPath != outputPath) File.Copy(inputPath, outputPath, true);
                return;
            }

            VertexMerger merger = new VertexMerger(1e-2f);
            List<int> indices = new List<int>();

            using (BinaryReader br = new BinaryReader(File.OpenRead(inputPath)))
            {
                br.BaseStream.Seek(80, SeekOrigin.Begin);
                uint triCount = br.ReadUInt32();
                
                for (int i = 0; i < triCount; i++)
                {
                    br.BaseStream.Seek(12, SeekOrigin.Current); 
                    for (int v = 0; v < 3; v++)
                    {
                        Vector3 vec = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        indices.Add(merger.AddVertex(vec));
                    }
                    br.BaseStream.Seek(2, SeekOrigin.Current);
                }
            }

            Vector3[] originalVertices = merger.Vertices.ToArray();
            int vertexCount = originalVertices.Length;

            Vector3 minOrig = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 maxOrig = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < vertexCount; i++)
            {
                minOrig = Vector3.Min(minOrig, originalVertices[i]);
                maxOrig = Vector3.Max(maxOrig, originalVertices[i]);
            }

            List<int>[] adj = new List<int>[vertexCount];
            for (int i = 0; i < vertexCount; i++) adj[i] = new List<int>();
            
            for (int i = 0; i < indices.Count; i += 3)
            {
                int i1 = indices[i], i2 = indices[i+1], i3 = indices[i+2];
                if (i1 == i2 || i2 == i3 || i1 == i3) continue;

                if (!adj[i1].Contains(i2)) adj[i1].Add(i2);
                if (!adj[i1].Contains(i3)) adj[i1].Add(i3);
                if (!adj[i2].Contains(i1)) adj[i2].Add(i1);
                if (!adj[i2].Contains(i3)) adj[i2].Add(i3);
                if (!adj[i3].Contains(i1)) adj[i3].Add(i1);
                if (!adj[i3].Contains(i2)) adj[i3].Add(i2);
            }

            Vector3[] p = new Vector3[vertexCount]; 
            Array.Copy(originalVertices, p, vertexCount);

            if (preMeltingSteps > 0)
            {
                for (int pm = 0; pm < preMeltingSteps; pm++)
                {
                    Vector3[] tempP = new Vector3[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        if (adj[i].Count == 0) { tempP[i] = p[i]; continue; }
                        Vector3 sum = Vector3.Zero;
                        foreach (int neighbor in adj[i]) sum += p[neighbor];
                        tempP[i] = sum / adj[i].Count; 
                    }
                    for (int i = 0; i < vertexCount; i++) p[i] = tempP[i];
                }            
            }

            Array.Copy(p, originalVertices, vertexCount);

            Vector3[] q = new Vector3[vertexCount]; 
            Vector3[] d = new Vector3[vertexCount]; 
            float alpha = 0.9f; 
            float beta = 0.1f;  

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    if (adj[i].Count == 0) { q[i] = p[i]; continue; }
                    Vector3 sum = Vector3.Zero;
                    foreach (int neighbor in adj[i]) sum += p[neighbor];
                    Vector3 centroid = sum / adj[i].Count;
                    q[i] = p[i] + alpha * (centroid - p[i]);
                }
                for (int i = 0; i < vertexCount; i++) d[i] = q[i] - originalVertices[i];
                for (int i = 0; i < vertexCount; i++)
                {
                    if (adj[i].Count == 0) { p[i] = q[i]; continue; }
                    Vector3 sumD = Vector3.Zero;
                    foreach (int neighbor in adj[i]) sumD += d[neighbor];
                    Vector3 avgD = sumD / adj[i].Count;
                    p[i] = q[i] - (beta * d[i] + (1.0f - beta) * avgD);
                }
            }

            Vector3 minSmooth = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 maxSmooth = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < vertexCount; i++)
            {
                minSmooth = Vector3.Min(minSmooth, p[i]);
                maxSmooth = Vector3.Max(maxSmooth, p[i]);
            }

            Vector3 sizeOrig = maxOrig - minOrig;
            Vector3 sizeSmooth = maxSmooth - minSmooth;

            Vector3 scale = new Vector3(
                sizeSmooth.X > 0.0001f ? sizeOrig.X / sizeSmooth.X : 1.0f,
                sizeSmooth.Y > 0.0001f ? sizeOrig.Y / sizeSmooth.Y : 1.0f,
                sizeSmooth.Z > 0.0001f ? sizeOrig.Z / sizeSmooth.Z : 1.0f
            );

            for (int i = 0; i < vertexCount; i++)
            {
                p[i].X = minOrig.X + (p[i].X - minSmooth.X) * scale.X;
                p[i].Y = minOrig.Y + (p[i].Y - minSmooth.Y) * scale.Y;
                p[i].Z = minOrig.Z + (p[i].Z - minSmooth.Z) * scale.Z;
            }

            using (BinaryWriter bw = new BinaryWriter(File.Open(outputPath, FileMode.Create)))
            {
                bw.Write(new byte[80]); 
                bw.Write((uint)(indices.Count / 3));

                for (int i = 0; i < indices.Count; i += 3)
                {
                    int i1 = indices[i], i2 = indices[i+1], i3 = indices[i+2];
                    Vector3 v1 = p[i1], v2 = p[i2], v3 = p[i3];

                    Vector3 normal = Vector3.Cross(v2 - v1, v3 - v1);
                    if (normal.LengthSquared() > 1e-8f) normal = Vector3.Normalize(normal);
                    else normal = Vector3.Zero;

                    bw.Write(normal.X); bw.Write(normal.Y); bw.Write(normal.Z);
                    bw.Write(v1.X); bw.Write(v1.Y); bw.Write(v1.Z);
                    bw.Write(v2.X); bw.Write(v2.Y); bw.Write(v2.Z);
                    bw.Write(v3.X); bw.Write(v3.Y); bw.Write(v3.Z);
                    bw.Write((ushort)0);
                }
            }
        }
    }
}