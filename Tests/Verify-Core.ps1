param()
$ErrorActionPreference = 'Stop'
$editorPath = Join-Path $PSScriptRoot '..\Assets\ResourceOrganizer\Editor'
$editorPath = (Resolve-Path -LiteralPath $editorPath).Path

# PowerShell 7 ships Roslyn. This verifies real C# syntax, not Unity API compatibility.
foreach ($assembly in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll')) {
    $assemblyPath = Join-Path $PSHOME $assembly
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw '需要包含 Roslyn 的 PowerShell 7 环境，或在 Unity 中执行编译验收。'
    }
    Add-Type -Path $assemblyPath
}
$files = @(Get-ChildItem -LiteralPath $editorPath -Filter '*.cs')
$windowTree = $null
$themeTree = $null
$mergeTree = $null
$partialWindowTrees = @()
foreach ($file in $files) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
        [IO.File]::ReadAllText($file.FullName), $null, $file.FullName,
        [Text.Encoding]::UTF8, [Threading.CancellationToken]::None)
    $errors = @($tree.GetDiagnostics() | Where-Object { $_.Severity.ToString() -eq 'Error' })
    if ($errors.Count -gt 0) { throw ($errors -join "`n") }
    if ($file.Name -eq 'ResourceOrganizerWindow.cs') { $windowTree = $tree }
    if ($file.Name -eq 'ResourceTheme.cs') { $themeTree = $tree }
    if ($file.Name -eq 'MergeService.cs') { $mergeTree = $tree }
    if ($file.Name -like 'ResourceOrganizerWindow.*.cs') { $partialWindowTrees += $tree }
}
Write-Output "C# syntax: $($files.Count) files passed."

Add-Type -Path (Join-Path $editorPath 'ResourceCore.cs')
$script:checks = 0
function Assert-Rule([bool] $condition, [string] $label) {
    if (-not $condition) { throw "FAILED: $label" }
    $script:checks++
}

Assert-Rule ([Suyao.ResourceOrganizer.ResourcePath]::Normalize(' Assets\Art\ ') -eq 'Assets/Art') 'Normalize Windows paths'
Assert-Rule ([Suyao.ResourceOrganizer.ResourcePath]::IsWithin('Assets/Art/a.png', 'Assets/Art')) 'Descendant boundary'
Assert-Rule (-not [Suyao.ResourceOrganizer.ResourcePath]::IsWithin('Assets/Artwork/a.png', 'Assets/Art')) 'Sibling prefix must not match'
Assert-Rule ([Suyao.ResourceOrganizer.ResourcePath]::Related('Assets/Art', 'assets/art/sub')) 'Parent child and case-insensitive collision'
Assert-Rule (-not [Suyao.ResourceOrganizer.ResourcePath]::IsAsset('Assets/../Outside/a.png')) 'Reject traversal'
Assert-Rule (-not [Suyao.ResourceOrganizer.ResourcePath]::IsAsset('Packages/Test/a.png')) 'Reject package mutation'
Assert-Rule (-not [Suyao.ResourceOrganizer.ResourcePath]::IsAsset('Assets')) 'Reject Assets root mutation'
Assert-Rule ([Suyao.ResourceOrganizer.ResourcePath]::CanCleanType('Assets/a.PNG')) 'Art file whitelist ignores extension case'
Assert-Rule (-not [Suyao.ResourceOrganizer.ResourcePath]::CanCleanType('Assets/a.asset')) 'Unknown native assets remain protected'
Assert-Rule (-not [Suyao.ResourceOrganizer.ResourcePath]::CanCleanType('Assets/a.cs')) 'Scripts remain protected'
$occupied = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void] $occupied.Add('Assets/Art/A_1.png')
Assert-Rule ([Suyao.ResourceOrganizer.ResourcePath]::Unique('Assets/Art/a.png', $occupied) -eq 'Assets/Art/a_2.png') 'Rename cannot collide on Windows'

$deps = [Collections.Generic.Dictionary[string,string[]]]::new([StringComparer]::Ordinal)
$protected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$deps.Add('scene', [string[]]@('prefab'))
$deps.Add('prefab', [string[]]@('material'))
$deps.Add('material', [string[]]@('texture'))
$deps.Add('texture', [string[]]@())
$decision = [Suyao.ResourceOrganizer.CleanupGraph]::Evaluate([string[]]@('material', 'texture'), $deps, $protected)
Assert-Rule ($decision.Allowed.Count -eq 0) 'External scene to prefab protects material and texture'
Assert-Rule ($decision.Blocked.ContainsKey('texture')) 'Protection propagates through blocked candidate'

$deps.Clear()
$deps.Add('A', [string[]]@('B'))
$deps.Add('B', [string[]]@('A', 'texture'))
$deps.Add('texture', [string[]]@())
$decision = [Suyao.ResourceOrganizer.CleanupGraph]::Evaluate([string[]]@('A', 'B', 'texture'), $deps, $protected)
Assert-Rule ($decision.Allowed.Count -eq 3) 'Candidate-only dependency cycle can be removed as a group'
$protected.Add('A', 'Addressables')
$decision = [Suyao.ResourceOrganizer.CleanupGraph]::Evaluate([string[]]@('A', 'B', 'texture'), $deps, $protected)
Assert-Rule ($decision.Allowed.Count -eq 0) 'Dynamic root protects entire cyclic dependency closure'

$protected.Clear()
$deps.Clear()
$deps.Add('folder', [string[]]@('texture'))
$deps.Add('texture', [string[]]@())
$decision = [Suyao.ResourceOrganizer.CleanupGraph]::Evaluate([string[]]@('texture'), $deps, $protected)
Assert-Rule ($decision.Allowed.Count -eq 0) 'Retained folder reference protects its contents'

# Imported output is not a saveable native asset; importer settings remain protected.
foreach ($path in @('Assets/Art/effect.shader', 'Assets/Fonts/default.ttf', 'Assets/Art/a.png', 'Assets/Models/a.fbx')) {
    Assert-Rule (-not [Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave($path, $false, $false)) "Imported output does not block: $path"
}
foreach ($path in @('Assets/Art/a.mat', 'Assets/Config/a.asset', 'Assets/Prefabs/a.prefab', 'Assets/Art/a.anim')) {
    Assert-Rule ([Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave($path, $false, $true)) "Native unsaved asset remains protected: $path"
}
Assert-Rule ([Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave('Assets/Fonts/default.ttf', $true, $false)) 'Dirty importer settings still block'
Assert-Rule ([Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave('Packages/Test/config.asset', $false, $true)) 'Native package edits remain protected'
Assert-Rule (-not [Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave('Library/cache.asset', $false, $true)) 'Internal cache does not block'
Assert-Rule (-not [Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave('', $true, $true)) 'Objects without asset paths do not block'

# Naming rules are independent of Unity; execute edge cases directly.
Assert-Rule ([Suyao.ResourceOrganizer.AssetNaming]::Compose('tree','','art_','_','tree','leaf',3,1,2) -eq 'art_leaf_03') 'Rename prefix, suffix, replacement and numeric padding'
Assert-Rule ([Suyao.ResourceOrganizer.AssetNaming]::Compose('tree','','','','','',27,2,3) -eq 'treeaa') 'Alphabetic numbering continues after z'
Assert-Rule ([Suyao.ResourceOrganizer.AssetNaming]::Compose('old','new','','','','',1,0,3) -eq 'new') 'Unified base name'
foreach ($name in @('', 'CON', 'LPT9', 'bad/name', 'bad*name', 'trailing.', ' spaced ')) {
    Assert-Rule (-not [Suyao.ResourceOrganizer.AssetNaming]::ValidName($name)) "Reject unsafe asset name: $name"
}
Assert-Rule ([Suyao.ResourceOrganizer.AssetNaming]::ValidName('植物_leaf_01')) 'Unicode asset names are valid'

# Regression for the reported FR2 cache blocking a normal folder merge.
$cachePath = 'Assets/Editor/FindReference2/FR2_Cache.asset'
Assert-Rule (-not [Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave($cachePath, $false, $true, $true, 'FR2_Cache')) 'Merge ignores dirty known FR2 index'
Assert-Rule ([Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave($cachePath, $false, $true, $false, 'FR2_Cache')) 'Explicit strict mode and unsaved-items display retain FR2 cache'
Assert-Rule ([Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave($cachePath, $true, $false, $true, 'FR2_Cache')) 'FR2 importer settings do not bypass saving'
Assert-Rule ([Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave($cachePath, $false, $true, $true, 'Material')) 'Spoofed cache filename remains protected'
Assert-Rule ([Suyao.ResourceOrganizer.UnsavedAssetPolicy]::RequiresSave('Assets/Config/FR2_Cache.asset', $false, $true, $true, 'FR2_Cache')) 'FR2 type outside known directory remains protected'
foreach ($pair in @(@('Assets/a.PREFAB','Prefabs'), @('Assets/a.FBX','Models'), @('Assets/a.mat','Materials'), @('Assets/a.PNG','Textures'), @('Assets/a.controller','Animations'), @('Assets/a.ogg','Audio'), @('Assets/a.asset','Other'))) {
    Assert-Rule ([Suyao.ResourceOrganizer.AssetCategory]::Folder($pair[0]) -eq $pair[1]) "Classification: $($pair[0])"
}
$folderPath = 'Assets/GameAssets/shared/scenes/furniture_surface/prefabs/plant'
$mergeBindings = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$cleanupFolders = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
Assert-Rule ([Suyao.ResourceOrganizer.FolderReferencePolicy]::IsFr2Index($cachePath, 'FR2_Cache')) 'Recognize reported FR2 index path and type'
Assert-Rule (-not [Suyao.ResourceOrganizer.FolderReferencePolicy]::IsFr2Index($cachePath, 'RuntimeSettings')) 'Same filename with runtime type must not bypass protection'
Assert-Rule (-not [Suyao.ResourceOrganizer.FolderReferencePolicy]::IsFr2Index('Assets/Game/FR2_Cache.asset', 'FR2_Cache')) 'Unrecognized location must not bypass protection'
Assert-Rule (-not [Suyao.ResourceOrganizer.FolderReferencePolicy]::IsFr2Index('Assets/Editor/FindReference2/Other.asset', 'FR2_Cache')) 'Other asset names remain protected'
[Suyao.ResourceOrganizer.FolderReferencePolicy]::Record($cachePath, $folderPath, $mergeBindings, $cleanupFolders, 'FR2_Cache')
Assert-Rule (-not $mergeBindings.ContainsKey($folderPath)) 'Cache-only directory edge no longer blocks merge'
Assert-Rule ($cleanupFolders.Contains($folderPath)) 'Cache directory edge remains in cleanup graph'
[Suyao.ResourceOrganizer.FolderReferencePolicy]::Record('Assets/Config/RealFolderBinding.asset', $folderPath, $mergeBindings, $cleanupFolders)
Assert-Rule ($mergeBindings.ContainsKey($folderPath)) 'Real directory binding still blocks merge'
[Suyao.ResourceOrganizer.FolderReferencePolicy]::Record($cachePath, $folderPath, $mergeBindings, $cleanupFolders, 'FR2_Cache')
Assert-Rule ($mergeBindings[$folderPath] -eq 'Assets/Config/RealFolderBinding.asset') 'Cache cannot erase an existing real binding'
$deps.Clear()
$protected.Clear()
$deps.Add($cachePath, [string[]]@($folderPath))
$deps.Add($folderPath, [string[]]@('Assets/Art/texture.png'))
$deps.Add('Assets/Art/texture.png', [string[]]@())
$decision = [Suyao.ResourceOrganizer.CleanupGraph]::Evaluate([string[]]@('Assets/Art/texture.png'), $deps, $protected)
Assert-Rule ($decision.Allowed.Count -eq 0) 'FR2 cache dependencies still conservatively protect assets during cleanup'

# Regression guard: scene querying and cleanup must not rely on each other's state.
$methods = @{}
$getRoot = [Microsoft.CodeAnalysis.SyntaxTree].GetMethod('GetRoot', [type[]]@([Threading.CancellationToken]))
$windowRoot = $getRoot.Invoke($windowTree, [object[]]@([Threading.CancellationToken]::None))
foreach ($node in $windowRoot.DescendantNodes($null, $false)) {
    if ($node.GetType().Name -eq 'MethodDeclarationSyntax') {
        $methods[$node.Identifier.ValueText] = $node
    }
}
foreach ($methodName in @('DrawCleanup', 'DrawCleanupResults', 'StartLoadCleanup', 'StartCleanup', 'TrashSelected')) {
    $identifiers = @($methods[$methodName].DescendantNodes($null, $false) |
        Where-Object { $_.GetType().Name -eq 'IdentifierNameSyntax' } |
        ForEach-Object { $_.Identifier.ValueText })
    Assert-Rule (-not ($identifiers -contains 'query')) "$methodName does not depend on scene query state"
}
$splitIdentifiers = @($methods['DrawSplit'].DescendantNodes($null, $false) |
    Where-Object { $_.GetType().Name -eq 'IdentifierNameSyntax' } | ForEach-Object { $_.Identifier.ValueText })
Assert-Rule (($splitIdentifiers -contains 'leftScroll') -and ($splitIdentifiers -contains 'rightScroll')) 'Split layout uses distinct scroll state for each pane'
foreach ($methodName in @('DrawReferences', 'DrawReferenceConfiguration', 'DrawReferenceResults', 'StartQuery', 'InvalidateReferences', 'ExportQuery')) {
    $identifiers = @($methods[$methodName].DescendantNodes($null, $false) |
        Where-Object { $_.GetType().Name -eq 'IdentifierNameSyntax' } |
        ForEach-Object { $_.Identifier.ValueText })
    Assert-Rule (-not ($identifiers -contains 'cleanup') -and -not ($identifiers -contains 'cleanupCandidates') -and
        -not ($identifiers -contains 'InvalidateCleanup')) "$methodName does not touch cleanup state"
}

# Catch wrapper-call argument omissions such as the reported CS7036 without
# Guard the local-only merge boundary and prevent unchecked folder deletion.
$mergeRoot = $getRoot.Invoke($mergeTree, [object[]]@([Threading.CancellationToken]::None))
$mergeIdentifiers = @($mergeRoot.DescendantNodes($null, $false) |
    Where-Object { $_.GetType().Name -eq 'IdentifierNameSyntax' } |
    ForEach-Object { $_.Identifier.ValueText })
Assert-Rule (-not ($mergeIdentifiers -contains 'ProjectIndex') -and -not ($mergeIdentifiers -contains 'GetAllAssetPaths') -and
    -not ($mergeIdentifiers -contains 'GetDependencies')) 'Merge service does not scan the project or build a dependency index'
Assert-Rule (-not ($mergeIdentifiers -contains 'MoveAssetToTrash') -and -not ($mergeIdentifiers -contains 'DeleteAsset')) 'Local merge preserves source folder identities without unchecked deletion'
$previewIdentifiers = @($methods['StartMergePreview'].DescendantNodes($null, $false) |
    Where-Object { $_.GetType().Name -eq 'IdentifierNameSyntax' } |
    ForEach-Object { $_.Identifier.ValueText })
Assert-Rule (-not ($previewIdentifiers -contains 'ProjectIndex') -and -not ($previewIdentifiers -contains 'Build')) 'Merge preview starts without a full project index'

# Catch wrapper-call argument omissions such as the reported CS7036 without
# pretending this is a full Unity type check. Check all window theme calls.
$themeRoot = $getRoot.Invoke($themeTree, [object[]]@([Threading.CancellationToken]::None))
$themeMethods = @{}
foreach ($node in $themeRoot.DescendantNodes($null, $false)) {
    if ($node.GetType().Name -ne 'MethodDeclarationSyntax') { continue }
    $methodName = $node.Identifier.ValueText
    $parameters = @($node.ParameterList.Parameters)
    $required = @($parameters | Where-Object { $null -eq $_.Default -and -not $_.ToString().StartsWith('params ') }).Count
    $variadic = @($parameters | Where-Object { $_.ToString().StartsWith('params ') }).Count -gt 0
    if (-not $themeMethods.ContainsKey($methodName)) { $themeMethods[$methodName] = @() }
    $themeMethods[$methodName] += [PSCustomObject]@{ Required = $required; Maximum = $parameters.Count; Variadic = $variadic }
}
$invalidCalls = @()
$allWindowNodes = @($windowRoot.DescendantNodes($null, $false))
foreach ($partialTree in $partialWindowTrees) {
    $partialRoot = $getRoot.Invoke($partialTree, [object[]]@([Threading.CancellationToken]::None))
    $allWindowNodes += @($partialRoot.DescendantNodes($null, $false))
}
foreach ($node in $allWindowNodes) {
    if ($node.GetType().Name -ne 'InvocationExpressionSyntax' -or
        $node.Expression.GetType().Name -ne 'MemberAccessExpressionSyntax' -or
        $node.Expression.Expression.ToString() -ne 'ResourceTheme') { continue }
    $methodName = $node.Expression.Name.Identifier.ValueText
    $argumentCount = $node.ArgumentList.Arguments.Count
    $compatible = @($themeMethods[$methodName] | Where-Object {
        $argumentCount -ge $_.Required -and ($_.Variadic -or $argumentCount -le $_.Maximum)
    })
    if ($compatible.Count -eq 0) { $invalidCalls += $node.ToString() }
}
Assert-Rule ($invalidCalls.Count -eq 0) ('Theme wrapper argument counts: ' + ($invalidCalls -join '; '))
Write-Output "Core compilation and safety rules: $script:checks passed."
Write-Output 'Not performed: Unity Editor compilation, imports, UI, GUID moves, Addressables, OS trash integration.'
