# CaboCam

Use a câmera do iPhone (traseira 1×, ultra-angular 0.5×, teleobjetiva ou frontal) como webcam no Windows 11, conectada **pelo cabo USB**.

- **App do iPhone** (`ios/`): captura com AVFoundation, codifica em H.264/HEVC no chip do iPhone e envia pelo cabo.
- **App do Windows** (`windows/desktop/`): conecta ao iPhone, configura câmera, resolução, zoom, foco, exposição etc. e mostra a imagem.
- **Câmera virtual** (`windows/native/`): cria a webcam **"CaboCam"**, que aparece no Teams, Zoom, Meet/Chrome, OBS e no app Câmera.

Detalhes técnicos: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · [docs/PROTOCOL.md](docs/PROTOCOL.md)

## Requisitos

| Onde | O quê |
|---|---|
| iPhone | iOS 17 ou superior |
| Mac (para instalar o app no iPhone) | Xcode (App Store) + uma conta Apple (a conta gratuita funciona, mas o app expira em 7 dias) |
| Windows | Windows 11 + app **Dispositivos Apple** (Microsoft Store) ou iTunes, que fornece o driver USB do iPhone |

## 1. Instalar no iPhone

### Pelo app do Windows (sem Mac)

O workflow `.github/workflows/ios.yml` compila no GitHub Actions (macOS) um `CaboCam-iOS-unsigned.ipa` sem assinatura e o anexa ao Release quando você cria uma tag `v*`. O app do Windows assina e instala esse `.ipa` com o seu Apple ID, usando o [plumesign](https://github.com/claration/impactor) (CLI do Impactor, licença MIT), que vem junto do CaboCam.

1. Conecte o iPhone no cabo e ative **Ajustes → Privacidade e Segurança → Modo de Desenvolvedor**.
2. No CaboCam do Windows, no card **App do iPhone**, clique em **Renovar / atualizar app**.
3. Na primeira vez, entre com o Apple ID e digite o código de verificação. A senha não é salva. Só a sessão da Apple fica guardada em `%AppData%\PlumeImpactor\accounts.json`. Prefira um Apple ID secundário.
4. Com conta gratuita, o app expira em **7 dias**. O card mostra a data e avisa 2 dias antes. Para renovar, basta clicar de novo com o iPhone no cabo.

O botão **Instalar .ipa…** assina um `.ipa` local, por exemplo o artefato de um build que ainda não virou Release.

### Pelo Xcode (Mac)

```bash
brew install xcodegen
```

```bash
cd ios && xcodegen generate && open CaboCam.xcodeproj
```

No Xcode: *Signing & Capabilities* → escolha seu *Team* → conecte o iPhone → ▶︎ Run.
Veja [ios/README.md](ios/README.md).

## 2. Compilar e instalar no Windows

O build roda no GitHub Actions (`.github/workflows/windows.yml`). Envie o projeto para um repositório seu no GitHub. Em *Actions*, baixe o artefato **CaboCam-Windows-x64**. Se você criar uma tag `v1.0.0`, o workflow também publica um Release com o zip.

No PC, descompacte o arquivo, clique com o botão direito em `install.ps1` e escolha **Executar com o PowerShell**. O script pede permissão de administrador uma única vez, para registrar a câmera virtual.

## 3. Usar

1. Conecte o iPhone no cabo, desbloqueie e toque em **Confiar neste computador**.
2. Abra o **CaboCam** no iPhone e mantenha o app aberto em primeiro plano. Use "Escurecer tela" para economizar bateria.
3. Abra o **CaboCam** no Windows: ele conecta sozinho e começa a transmitir.
4. No Teams, Zoom ou outro app, escolha a câmera **CaboCam**.

## Status

O código foi escrito sem compilar no Windows nem no iOS, porque este Mac não tem Xcode nem MSVC. Apenas o app desktop em C# compilou aqui. Espere uma rodada de correções no primeiro build do GitHub Actions e no primeiro build do Xcode.
