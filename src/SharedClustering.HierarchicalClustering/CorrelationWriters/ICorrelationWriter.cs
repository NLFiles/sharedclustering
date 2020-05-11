﻿using SharedClustering.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SharedClustering.HierarchicalClustering.CorrelationWriters
{
    /// <summary>
    /// Write a correlation matrix to an external file.
    /// </summary>
    public interface ICorrelationWriter : IDisposable
    {
        int MaxColumns { get; }

        IDisposable BeginWriting();
        Task<List<string>> OutputCorrelationAsync(
            List<ClusterNode> nodes,
            Dictionary<int, IClusterableMatch> matchesByIndex,
            Dictionary<int, int> indexClusterNumbers,
            List<Tag> tags,
            string worksheetName);
        string SaveFile(int fileNum);
    }
}
