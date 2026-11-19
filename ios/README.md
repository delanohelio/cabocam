# CaboCam para iPhone

App iOS (Swift 5.9+, SwiftUI, iOS 17+) que transforma a câmera do iPhone em webcam para o
CaboCam do Windows 11. O iPhone atua como servidor TCP na porta **7878**; o app do Windows
conecta pelo cabo (usbmuxd) ou pela rede local. Protocolo: `../docs/PROTOCOL.md`.

## Como compilar

Pré-requisitos: Mac com Xcode 15 ou mais novo e um iPhone com iOS 17+ (a câmera não funciona
no Simulador).

```sh
brew install xcodegen
cd ios
xcodegen generate
open CaboCam.xcodeproj
```

No Xcode:

1. Selecione o target **CaboCam** > **Signing & Capabilities**.
2. Em **Team**, escolha o seu Apple ID / equipe (o `project.yml` deixa `DEVELOPMENT_TEAM` vazio).
   Se o bundle id `com.cabocam.ios` já estiver em uso por outra conta, troque-o por um único seu.
3. Conecte o iPhone, escolha-o como destino de execução e clique em **Run**.
4. No iPhone, confie no desenvolvedor em Ajustes > Geral > VPN e Gerenciamento de Dispositivo
   (primeira vez) e aceite as permissões de **Câmera** e **Rede Local**.

Observação: com um Apple ID gratuito o perfil de provisionamento **expira após 7 dias**; depois
disso basta executar o app de novo pelo Xcode para renovar. Com a conta paga do Apple Developer
Program a validade é de 1 ano.

`CaboCam.xcodeproj` e `CaboCam/Info.plist` são gerados pelo XcodeGen a partir do `project.yml`
(não são versionados). Rode `xcodegen generate` sempre que mudar o `project.yml` ou
adicionar/remover arquivos.

## Uso

- Abra o app e deixe o iPhone **desbloqueado e em primeiro plano** (o iOS suspende a câmera em
  segundo plano). A tela fica sempre ligada; use **Escurecer tela** para economizar bateria
  (toque duas vezes para sair).
- No PC, abra o CaboCam, conecte o cabo e aceite "Confiar neste computador" no iPhone.
- A pré-visualização funciona assim que o app abre; o vídeo só é enviado depois do comando
  `start` do computador.

## Estrutura

| Arquivo | Função |
|---|---|
| `CaboCam/CaboCamApp.swift` | Ponto de entrada, ciclo de vida (primeiro plano / segundo plano) |
| `CaboCam/Networking/Protocol.swift` | Enquadramento dos pacotes, mensagens JSON (Codable) |
| `CaboCam/Networking/StreamServer.swift` | `NWListener` na porta 7878, um cliente por vez, backpressure |
| `CaboCam/Capture/CameraManager.swift` | `AVCaptureSession`, descoberta de câmeras, formatos e todos os controles |
| `CaboCam/Capture/VideoEncoder.swift` | `VTCompressionSession` H.264/HEVC, conversão AVCC para Annex-B |
| `CaboCam/State/AppModel.swift` | Estado, despacho de mensagens, `state` periódico, bateria/temperatura |
| `CaboCam/UI/ContentView.swift` | Interface em português (pré-visualização, lentes, zoom, lanterna, escurecer) |
