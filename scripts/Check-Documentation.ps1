#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$files = @((Get-ChildItem -LiteralPath $root -File -Filter '*.md')) + @((Get-ChildItem -LiteralPath (Join-Path $root 'docs') -File -Filter '*.md' -Recurse))
$broken = [Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($link in [regex]::Matches($content, '\]\(([^)]+)\)')) {
        $target = $link.Groups[1].Value.Trim('<', '>')
        if ($target -match '^(https?://|#)') { continue }
        $path = [Uri]::UnescapeDataString($target.Split('#')[0])
        if (!(Test-Path -LiteralPath (Join-Path $file.DirectoryName $path))) {
            $broken.Add("$([IO.Path]::GetRelativePath($root, $file.FullName)): $target")
        }
    }
}
if ($broken.Count) { throw ($broken -join [Environment]::NewLine) }
Write-Output "Verified local links in $($files.Count) Markdown documents. External URLs and anchors are not fetched."
