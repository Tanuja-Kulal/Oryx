﻿// --------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// --------------------------------------------------------------------------------------------

namespace Microsoft.Oryx.BuildScriptGenerator
{
    /// <summary>
    /// Detects language name and version of the application in source directory.
    /// </summary>
    public interface ILanguageDetector
    {
        /// <summary>
        /// Detects language name and version of the application in source directory.
        /// </summary>
        /// <param name="sourceRepo">The <see cref="ISourceRepo"/> to detect.</param>
        /// <returns>An instance of <see cref="LanguageDetectorResult"/> if detection was
        /// successful, <c>null</c> otherwise</returns>
        LanguageDetectorResult Detect(ISourceRepo sourceRepo);
    }
}