# Extract specific CSS rules from Codex CSS for key UI components
$css = [System.IO.File]::ReadAllText('C:\Users\54xzh\AppData\Local\Temp\codex-extracted\src\webview\assets\index-DYqVWCHk.css')

# Search for key patterns
$patterns = @(
    'electron-dark',
    'token-foreground',
    'token-background', 
    'sidebar-w',
    'sidebar-background',
    'composer',
    'inbox',
    'thread',
    'message',
    'chat-scroll',
    'conversation',
    'bg-background',
    'text-foreground',
    'prose',
    'markdown',
    'turn-',
    'agent-',
    '--sp:',
    'border-token-',
    'font-size:13',
    'color-token-',
    '--color-'
)

foreach ($pat in $patterns) {
    $idx = 0
    $found = 0
    while ($idx -lt $css.Length -and $found -lt 3) {
        $pos = $css.IndexOf($pat, $idx, [System.StringComparison]::OrdinalIgnoreCase)
        if ($pos -eq -1) { break }
        $start = [Math]::Max(0, $pos - 40)
        $end = [Math]::Min($css.Length, $pos + 200)
        $snippet = $css.Substring($start, $end - $start)
        Write-Output "==[${pat}]== $snippet"
        Write-Output "---"
        $found++
        $idx = $pos + $pat.Length
    }
}
