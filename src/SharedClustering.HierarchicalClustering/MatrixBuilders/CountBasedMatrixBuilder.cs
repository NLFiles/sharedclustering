﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SharedClustering.Core;

namespace SharedClustering.HierarchicalClustering.MatrixBuilders
{
    /// <summary>
    /// Build a matrix that is weighted as a count of total appearances.
    /// If two matches (A) and (B) do _not_ appear on each other's shared match lists,
    /// then each _indirectCorrelationValue increment of the matrix value represents one match list where they appear together,
    /// up to a maximum of _directCorrelationValue / 2.
    /// If two matches (A) and (B) _do_ appear on each other's shared match lists,
    /// then the matrix value is exactly _directCorrelationValue.
    /// 
    /// In other words, the higher the matrix value, the more likely two matches appear together,
    /// up to a flat maximum value if the matches appear on each other's shared match lists.
    /// </summary>
    internal class CountBasedMatrixBuilder : IMatrixBuilder
    {
        private readonly float _directCorrelationValue;
        private readonly float _indirectCorrelationValue;
        private readonly double _lowestClusterableCentimorgans;
        private readonly IProgressData _progressData;

        public CountBasedMatrixBuilder(float directCorrelationValue, float indirectCorrelationValue, double lowestClusterableCentimorgans, IProgressData progressData)
        {
            _directCorrelationValue = directCorrelationValue;
            _indirectCorrelationValue = indirectCorrelationValue;
            _lowestClusterableCentimorgans = lowestClusterableCentimorgans;
            _progressData = progressData;
        }

        public Task<ConcurrentDictionary<int, float[]>> CorrelateAsync(List<IClusterableMatch> clusterableMatches, List<IClusterableMatch> immediateFamily)
        {
            _progressData.Reset("Correlating data...", clusterableMatches.Count * 2);

            return Task.Run(async () =>
            {
                clusterableMatches = clusterableMatches.Skip(immediateFamily.Count).ToList();

                // Skip over any immediate family matches. Immediate family matches tend to have huge numbers of shared matches.
                // If the immediate family are included, the entire cluster diagram will get swamped with low-level
                // indirect matches (gray cells in the final), obscuring the useful clusters. 
                // The immediate family matches will still be included in the cluster diagram
                // by virtue of the other matches that are shared directly with them.
                var maxIndex = clusterableMatches.Where(match => match.Match.SharedCentimorgans >= _lowestClusterableCentimorgans).Max(match => Math.Max(match.Index, match.Coords.Max()));
                var matrix = new ConcurrentDictionary<int, float[]>();

                // For the immediate family, populate the matrix based only on direct shared matches.
                var immediateFamilyTasks = immediateFamily.Select(match => Task.Run(() =>
                {
                    ExtendMatrixDirect(matrix, match, maxIndex);
                    _progressData.Increment();
                    _progressData.Increment();
                }));
                await Task.WhenAll(immediateFamilyTasks);

                // For the other clusterable matches, first populate the matrix based on the indirect matches.
                var clusterableMatchesTasks = clusterableMatches.Select(match => Task.Run(() =>
                {
                    ExtendMatrixIndirect(matrix, match, maxIndex, _indirectCorrelationValue);
                    _progressData.Increment();
                }));
                await Task.WhenAll(clusterableMatchesTasks);

                // But make sure that the total indirect match value is no greater than half of the direct match value.
                var maxIndirectCorrectionValue = _directCorrelationValue / 2;
                var matrixTasks = matrix.Select(kvp => Task.Run(() =>
                {
                    for (var i = 0; i <= maxIndex; ++i)
                    {
                        kvp.Value[i] = Math.Min(kvp.Value[i], maxIndirectCorrectionValue);
                    }
                    _progressData.Increment();
                }));
                await Task.WhenAll(matrixTasks);

                clusterableMatchesTasks = clusterableMatches.Select(match => Task.Run(() =>
                {
                    ExtendMatrixDirect(matrix, match, maxIndex);
                    _progressData.Increment();
                }));
                await Task.WhenAll(clusterableMatchesTasks);

                _progressData.Reset("Done");

                return matrix;
            });
        }

        // An indirect match is when two matches A and B appear together on the shared match list of some other match C.
        // Matches are rated with a value of (n * indirectCorrelationValue),
        // where n is the number of shared match lists that contain both match A and match B.
        private static void ExtendMatrixIndirect(ConcurrentDictionary<int, float[]> matrix, IClusterableMatch match, int maxIndex, float indirectCorrelationValue)
        {
            foreach (var coord1 in match.Coords)
            {
                var row = matrix.GetOrAdd(coord1, _ => new float[maxIndex + 1]);

                if (coord1 == match.Index)
                {
                    if (coord1 < row.Length)
                    {
                        row[coord1] += indirectCorrelationValue;
                    }
                }
                else
                {
                    foreach (var coord2 in match.Coords.Where(coord2 => coord2 != match.Index && coord2 <= maxIndex))
                    {
                        row[coord2] += indirectCorrelationValue;
                    }
                }
            }
        }

        // A direct match is when match B appears on the shared match list of match A.
        // When the shared match list of match A contains match B, then matrix[A][B] is set to _directCorrelationValue.
        private void ExtendMatrixDirect(ConcurrentDictionary<int, float[]> matrix, IClusterableMatch match, int maxIndex)
        {
            if (match.Index <= maxIndex)
            {
                var row = matrix.GetOrAdd(match.Index, _ => new float[maxIndex + 1]);
                foreach (var coord2 in match.Coords.Where(coord2 => coord2 < row.Length))
                {
                    row[coord2] = _directCorrelationValue;
                }
            }
        }

        public void ExtendMatrix(
            ConcurrentDictionary<int, float[]> matrix,
            List<IClusterableMatch> clusterableMatches,
            int maxIndex)
        {
            foreach (var match in clusterableMatches)
            {
                ExtendMatrixDirect(matrix, match, maxIndex);
            }
        }
    }
}
