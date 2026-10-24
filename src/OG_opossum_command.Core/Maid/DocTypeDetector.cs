namespace OG_opossum_command.Core.Maid;

/// <summary>What a file IS, decided by format first and then by the words in it, with the reason spelled out.</summary>
public static class DocTypeDetector
{
    /// <summary>Doc types that are always kept, whatever the junk rules say: official, financial, legal (ADR-062).</summary>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.Ordinal)
    {
        "invoice", "receipt", "statement", "tax-form", "certificate", "contract", "medical", "form",
    };

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".exe"] = "installer", [".msi"] = "installer", [".msix"] = "installer", [".msixbundle"] = "installer", [".appx"] = "installer", [".dmg"] = "installer", [".pkg"] = "installer",
        [".zip"] = "archive", [".7z"] = "archive", [".rar"] = "archive", [".tar"] = "archive", [".gz"] = "archive", [".tgz"] = "archive",
        [".cs"] = "code", [".py"] = "code", [".js"] = "code", [".ts"] = "code", [".ps1"] = "code", [".sh"] = "code", [".java"] = "code", [".go"] = "code", [".rs"] = "code",
        [".cpp"] = "code", [".c"] = "code", [".h"] = "code", [".sql"] = "code", [".ipynb"] = "code",
        [".csv"] = "dataset", [".tsv"] = "dataset", [".json"] = "dataset", [".parquet"] = "dataset", [".xml"] = "dataset",
        [".psd"] = "design-asset", [".ai"] = "design-asset", [".svg"] = "design-asset", [".eps"] = "design-asset", [".fig"] = "design-asset", [".sketch"] = "design-asset", [".indd"] = "design-asset",
        [".mp4"] = "video", [".mov"] = "video", [".avi"] = "video", [".mkv"] = "video", [".webm"] = "video", [".wmv"] = "video",
        [".mp3"] = "audio", [".wav"] = "audio", [".m4a"] = "audio", [".flac"] = "audio", [".ogg"] = "audio",
        [".epub"] = "ebook", [".mobi"] = "ebook",
        [".pptx"] = "presentation", [".ppt"] = "presentation", [".pptm"] = "presentation", [".key"] = "presentation", [".odp"] = "presentation",
        [".ics"] = "schedule",
        [".ttf"] = "design-asset", [".otf"] = "design-asset", [".woff"] = "design-asset", [".woff2"] = "design-asset",
    };

    /// <summary>Content rules: doc type, then phrases that point at it. Two or more hits make the call.</summary>
    private static readonly (string Type, string[] Phrases)[] ContentRules =
    [
        ("invoice", ["invoice", "invoice number", "invoice no", "bill to", "amount due", "due date", "remit to", "payment terms", "balance due"]),
        ("receipt", ["receipt", "order number", "order #", "thank you for your purchase", "thank you for your order", "subtotal", "paid", "transaction id", "card ending"]),
        ("statement", ["statement", "account number", "opening balance", "closing balance", "statement period", "available balance", "account summary"]),
        ("tax-form", ["tax return", "w-2", "1099", "form 1040", "irs", "taxable income", "tax year", "withholding", "hmrc", "p60"]),
        ("certificate", ["certificate", "certify that", "this certifies", "awarded to", "has successfully completed", "certification", "in recognition of"]),
        ("contract", ["agreement", "lease", "tenant", "landlord", "hereby", "terms and conditions", "the parties", "effective date", "governing law", "signature", "witness"]),
        ("form", ["form", "please complete", "applicant", "date of birth", "signature of", "office use only", "print name", "application"]),
        ("resume", ["resume", "curriculum vitae", "work experience", "professional experience", "education", "skills", "references available", "objective"]),
        ("medical", ["patient", "diagnosis", "prescription", "physician", "dosage", "medical record", "lab results", "health plan", "explanation of benefits"]),
        ("ticket", ["boarding pass", "itinerary", "reservation", "confirmation number", "booking reference", "check-in", "e-ticket", "gate", "seat"]),
        ("schedule", ["schedule", "agenda", "timetable", "roster", "calendar", "shift", "session times", "run of show"]),
        ("report", ["report", "executive summary", "findings", "recommendations", "quarterly", "annual report", "analysis", "key metrics", "results"]),
        ("letter", ["dear", "sincerely", "yours truly", "kind regards", "to whom it may concern"]),
        ("presentation", ["slide", "agenda", "thank you", "questions"]),
    ];

    /// <summary>The doc type, a confidence from 0 to 1, and a reason.</summary>
    public static (string Type, double Confidence, string Reason) Detect(ScannedFile file, string lowerText, string lowerName)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(lowerText);
        ArgumentNullException.ThrowIfNull(lowerName);
        var extraction = file.Extraction;

        if (file.IsFolder)
        {
            return ("saved-web-page", 0.9, "a browser's saved-page assets folder");
        }

        if (file.Extension is ".crdownload" or ".download" or ".part" or ".partial" or ".tmp")
        {
            return ("partial-download", 0.95, $"{file.Extension} is an unfinished download");
        }

        if (extraction?.Image is { } image)
        {
            if (image.LooksLikeScreenshot)
            {
                return ("screenshot", 0.85, "a screenshot (name, software or screen-sized PNG, no camera)");
            }

            if (image.LooksLikeCameraPhoto)
            {
                return ("photo", 0.85, image.CameraModel is null ? "a camera photo (camera-style name)" : $"a photo from {image.CameraMake} {image.CameraModel}".Trim());
            }

            return (Mention(lowerName, "logo", "icon", "banner", "graphic") ? "design-asset" : "image", 0.6, "a picture with no camera details");
        }

        if (extraction?.IsSavedWebPage == true)
        {
            return ("saved-web-page", 0.85, "an HTML page saved by a browser");
        }

        // Content first for documents: a PDF can be anything.
        var haystack = lowerText.Length > 0 ? lowerText + " " + lowerName : lowerName;
        var best = ("document", 0, string.Empty);
        foreach (var (type, phrases) in ContentRules)
        {
            var hits = phrases.Where(p => TextAnalysis.Mentions(haystack, p)).ToList();
            var score = hits.Count + (TextAnalysis.Mentions(lowerName, type.Replace('-', ' ')) ? 2 : 0);
            if (score > best.Item2)
            {
                best = (type, score, string.Join(", ", hits.Take(3).Select(h => $"\"{h}\"")));
            }
        }

        if (ByExtension.TryGetValue(file.Extension, out var byExtension) && (best.Item2 < 3 || byExtension is "installer" or "archive" or "code" or "video" or "audio"))
        {
            return (byExtension, 0.9, $"{file.Extension} file");
        }

        if (best.Item2 >= 2)
        {
            var confidence = Math.Min(0.95, 0.5 + (0.1 * best.Item2));
            return (best.Item1, confidence, best.Item3.Length > 0 ? $"mentions {best.Item3}" : $"the name says {best.Item1}");
        }

        if (file.Extension is ".xlsx" or ".xlsm" or ".xls")
        {
            return ("dataset", 0.6, "a spreadsheet");
        }

        if (file.Extension is ".html" or ".htm" or ".mhtml" or ".mht")
        {
            return ("web-page", 0.6, "an HTML file");
        }

        return best.Item2 == 1 ? (best.Item1, 0.4, $"mentions {best.Item3}") : ("document", 0.3, "no strong signal of what it is");
    }

    private static bool Mention(string haystack, params string[] phrases) => phrases.Any(p => TextAnalysis.Mentions(haystack, p));
}
