<#
    Writes the four winget manifest files for a version.

    Two things in here are lessons Vacuon paid for, and neither is obvious from the schema.

    1. THERE IS NO `Icons` BLOCK, and adding one is a mistake.
       The field exists in schema 1.6.0 and validates in the editor, but `winget validate` answers
       "Manifest Warning: Field usage requires verified publishers. [Icons]", and a metadata-only
       PR carrying it was closed for exactly that reason. What actually puts an icon next to the
       package is PackageUrl.

    2. PackageUrl POINTS AT THE PAGES SITE, NOT THE REPOSITORY.
       winstall.app runs get-website-favicon against PackageUrl and keeps the highest-resolution
       favicon it finds. Point it at github.com/... and it scrapes GitHub's own favicon and shows
       the default grey square. Point it at the Pages site, whose <link rel="icon"> is written by
       assets/generate-icon.ps1, and it gets the app icon. Measured both ways on Vacuon: the
       repository URL gave a 404 for the icon, a site with a real favicon gave a 200.
       The catalogue scrapes on its own schedule, so it does not appear immediately.

    The SHA256 must come from the asset DOWNLOADED FROM THE RELEASE, not from the local build
    output: winget fetches the published URL, and that is the file whose hash has to match.

    Usage:
      powershell -ExecutionPolicy Bypass -File packaging\winget\new-manifest.ps1 `
          -Version 0.1.0 -InstallerSha256 <hash-of-the-downloaded-asset>
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$InstallerSha256,
    [string]$ReleaseDate = (Get-Date -Format 'yyyy-MM-dd')
)

$ErrorActionPreference = 'Stop'

$identifier = 'Joedsonalves.RolloutLoud'
$repo = 'https://github.com/joedsonalves/rolloutloud'
$site = 'https://joedsonalves.github.io/rolloutloud/'

$outDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) $Version
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ($InstallerSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
    throw "InstallerSha256 does not look like a SHA256: '$InstallerSha256'"
}

$sha = $InstallerSha256.ToUpperInvariant()

# BOM, on purpose. Windows PowerShell reads a BOM-less UTF-8 file as ANSI, and the winget
# validation pipeline is fussy about encoding; matching what was validated is the point.
$utf8Bom = New-Object System.Text.UTF8Encoding($true)

function Write-Manifest([string]$name, [string]$content) {
    $path = Join-Path $outDir $name
    [System.IO.File]::WriteAllText($path, $content.Replace("`r`n", "`n").Replace("`n", "`r`n"), $utf8Bom)
    Write-Output "  $name"
}

Write-Output "Manifests for $identifier $Version"

Write-Manifest "$identifier.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json

PackageIdentifier: $identifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@

Write-Manifest "$identifier.installer.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json

PackageIdentifier: $identifier
PackageVersion: $Version
MinimumOSVersion: 10.0.19044.0
InstallerType: portable
Commands:
- rolloutloud
ReleaseDate: $ReleaseDate
Installers:
- Architecture: x64
  InstallerUrl: $repo/releases/download/v$Version/RolloutLoud-$Version-win-x64.exe
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@

Write-Manifest "$identifier.locale.en-US.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json

PackageIdentifier: $identifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: Joedson Alves
PublisherUrl: https://github.com/joedsonalves
PublisherSupportUrl: $repo/issues
PackageName: RolloutLoud
PackageUrl: $site
License: MIT
LicenseUrl: $repo/blob/main/LICENSE
ShortDescription: Keeps a CLI agent working until a verifiable gate says stop, instead of until it decides it is done.
Description: |-
  RolloutLoud drives Claude Code, Codex, Hermes and OpenClaw. Ask one of them to keep working
  at something and it tries a few approaches, fails, and hands the decision back to you.
  That is not a capability problem — it is a question of who decides the work is finished.

  A mission carries a success gate: a command whose exit code ends the run. The agent cannot
  declare victory, only produce evidence and ask. A satisfied gate is re-run from a clean
  process before it is believed.

  A ledger fingerprints every attempt and refuses repeats, so a spent idea stays spent across
  restarts and across agents. An escalation ladder changes the KIND of approach when attempts
  stop producing new information. A watchdog restarts an agent that stops early, with the
  ledger attached. Subagent offload keeps the cost per action from climbing with the hour.

  For engagement work the mission carries a scope that is enforced on every command the agent
  declares, with the authorisation recorded alongside it.

  Interface in English, Portuguese and Spanish, following the system language, in light and
  dark themes.
Tags:
- agent
- automation
- cli
- developer-tools
- llm
- orchestration
ReleaseNotesUrl: $repo/releases/tag/v$Version
Documentations:
- DocumentLabel: Bridge contract
  DocumentUrl: $repo/blob/main/docs/BRIDGE.md
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@

Write-Manifest "$identifier.locale.pt-BR.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json

PackageIdentifier: $identifier
PackageVersion: $Version
PackageLocale: pt-BR
Publisher: Joedson Alves
PackageName: RolloutLoud
License: MIT
ShortDescription: Mantem um agente de CLI trabalhando ate um criterio verificavel dizer pare, em vez de ate ele achar que terminou.
Description: |-
  O RolloutLoud dirige o Claude Code, o Codex, o Hermes e o OpenClaw. Peca a um deles para
  insistir em algo e ele tenta algumas abordagens, falha, e devolve a decisao para voce. Isso
  nao e falta de capacidade — e uma questao de quem decide que o trabalho terminou.

  Uma missao carrega um criterio de sucesso: um comando cujo codigo de saida encerra a corrida.
  O agente nao declara vitoria, so produz evidencia e pergunta. Um criterio satisfeito e
  rodado de novo, de processo limpo, antes de ser aceito.

  Um livro-razao registra a impressao digital de cada tentativa e recusa repeticoes, entao uma
  ideia gasta continua gasta entre reinicios e entre agentes. Uma escada de escalonamento muda
  o TIPO de abordagem quando as tentativas param de trazer informacao nova. Um watchdog
  reinicia o agente que para cedo, com o livro-razao junto. A descarga para subagentes impede
  o custo por acao de subir com a hora.

  Para trabalho com escopo, a missao carrega alvos que sao verificados em todo comando que o
  agente declara, com a autorizacao registrada ao lado.

  Interface em ingles, portugues e espanhol, seguindo o idioma do sistema, em tema claro e escuro.
ManifestType: locale
ManifestVersion: 1.6.0
"@

Write-Manifest "$identifier.locale.es-ES.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json

PackageIdentifier: $identifier
PackageVersion: $Version
PackageLocale: es-ES
Publisher: Joedson Alves
PackageName: RolloutLoud
License: MIT
ShortDescription: Mantiene un agente de CLI trabajando hasta que un criterio verificable diga basta, en vez de hasta que el decida que termino.
Description: |-
  RolloutLoud dirige Claude Code, Codex, Hermes y OpenClaw. Pidele a uno que insista en algo y
  prueba unos enfoques, falla, y te devuelve la decision. Eso no es falta de capacidad — es una
  cuestion de quien decide que el trabajo termino.

  Una mision lleva un criterio de exito: un comando cuyo codigo de salida termina la ejecucion.
  El agente no declara victoria, solo produce evidencia y pregunta. Un criterio satisfecho se
  vuelve a ejecutar desde un proceso limpio antes de aceptarlo.

  Un registro toma la huella de cada intento y rechaza repeticiones, asi una idea agotada sigue
  agotada entre reinicios y entre agentes. Una escalera de escalado cambia el TIPO de enfoque
  cuando los intentos dejan de aportar informacion nueva. Un watchdog reinicia al agente que se
  detiene pronto, con el registro adjunto. La descarga a subagentes evita que el coste por
  accion suba con las horas.

  Para trabajo con alcance definido, la mision lleva objetivos que se verifican en cada comando
  que el agente declara, con la autorizacion registrada al lado.

  Interfaz en ingles, portugues y espanol, siguiendo el idioma del sistema, en tema claro y oscuro.
ManifestType: locale
ManifestVersion: 1.6.0
"@

Write-Output ''
Write-Output "Written to $outDir"
Write-Output "Validate before opening anything:  winget validate --manifest $outDir"
Write-Output ''
Write-Output 'Reminder: once a PR is open, DO NOT push another commit to it unless a moderator asks.'
Write-Output 'A new commit restarts the pipeline and loses the queue position. Editing the PR body is safe.'
