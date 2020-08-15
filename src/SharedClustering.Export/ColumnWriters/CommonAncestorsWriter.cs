﻿using OfficeOpenXml;
using SharedClustering.HierarchicalClustering;

namespace SharedClustering.Export.ColumnWriters
{
    public class CommonAncestorsWriter : IColumnWriter
    {
        public string Header => "Common Ancestors";
        public bool RotateHeader => true;
        public bool IsAutofit => false;
        public bool IsDecimal => false;
        public double Width => 3;

        public void WriteValue(ExcelRange cell, IClusterableMatch match, LeafNode leafNode) 
            => cell.Value = match.Match.CommonAncestors?.Count > 0 
                ? string.Join(", ", match.Match.CommonAncestors) 
                : null;

        public void ApplyConditionalFormatting(ExcelWorksheet ws, ExcelAddress excelAddress) { }
    }
}
