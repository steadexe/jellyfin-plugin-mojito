# Jellyfin Mojito

Plugin Jellyfin (12.x) qui résout un film ou une série via **Radarr/Sonarr**,
sélectionne la release correspondant au **quality profile** configuré, la pousse
vers **qBittorrent** en téléchargement séquentiel (taggé et catégorisé), et
lance la lecture dans Jellyfin **pendant que le torrent se télécharge**.

## Fonctionnement

```
Recherche "Dune 2" (page Mojito ou via l'API)
  → Radarr lookup TMDB + recherche interactive des releases
  → filtrage/score selon le quality profile (configurable)
  → magnet poussé vers qBittorrent : sequentialDownload + firstLastPiecePrio,
     tag "mojito" + tag de session, catégorie "mojito"
  → le bridge du plugin sert le préfixe téléchargé en HTTP chunked
  → Jellyfin transcode/remuxe et la lecture démarre dès le tampon atteint
  → à l'arrêt de la lecture : pause / suppression / seed (configurable)
```

## Prérequis

- Jellyfin 12.x (serveur en .NET 10)
- Radarr et/ou Sonarr avec des indexers configurés (généralement via Prowlarr)
- qBittorrent avec l'API Web activée
- **Un dossier de téléchargement visible à la fois par le process Jellyfin et
  qBittorrent** (même machine ou montage commun)
- SDK .NET 10 pour compiler

## Compilation et installation

```bash
dotnet build Jellyfin.Plugin.Mojito/Jellyfin.Plugin.Mojito.csproj -c Release
```

Copier `Jellyfin.Plugin.Mojito.dll` (et `build.yaml`/manifest dans un ZIP pour
le dépôt de plugins) dans le dossier `plugins/` de Jellyfin (sous-dossier
`Mojito_0.1.0.0` par exemple), puis redémarrer Jellyfin.

## Installation depuis un dépôt de plugins Jellyfin

Le dépôt git sert directement de plugin repository : Jellyfin lit le
`manifest.json` à sa racine et installe les versions depuis les releases.

1. Le dépôt GitHub `steadexe/jellyfin-plugin-mojito` publie automatiquement :
   chaque push sur `main` déclenche le workflow `.github/workflows/release.yml`
   (tests, build, ZIP, release GitHub, mise à jour du `manifest.json`) si la
   version de `build.yaml` n'est pas déjà publiée. Pour publier une nouvelle
   version : incrémenter `version` dans `build.yaml`, commit, push.
2. Dans Jellyfin : **Dashboard → Plugins → Repositories → +**, ajouter
   l'URL brute du manifest :

   ```
   https://raw.githubusercontent.com/steadexe/jellyfin-plugin-mojito/main/manifest.json
   ```

3. **Dashboard → Plugins → Catalog** : Mojito apparaît et s'installe ; les
   nouvelles versions sont proposées en mise à jour.

Pour un hébergement sans GitHub Actions, le packaging manuel équivalent :

```bash
python3 scripts/build_release.py                                   # build + ZIP
python3 scripts/build_release.py --update-manifest \
  --zip artifacts/Jellyfin.Plugin.Mojito_0.1.0.0.zip \
  --source-url https://exemple.org/mojito/Jellyfin.Plugin.Mojito_0.1.0.0.zip
```

Le ZIP d'installation contient `Jellyfin.Plugin.Mojito.dll` + `build.yaml`
(`targetAbi` 12.0.0.0, framework net10.0). Le checksum du manifest est le MD5
hex du ZIP, au format attendu par Jellyfin
(https://jellyfin.org/posts/plugin-updates/).

## Configuration

Dashboard → Plugins → Mojito : URLs et clés API Radarr/Sonarr, URL et
identifiants qBittorrent, dossier de téléchargement, **tag et catégorie
qBittorrent** (tous configurables), quality profiles films/séries, seuil de
tampon avant lecture, action après lecture (pause/suppression/seed), conservation
du média dans la bibliothèque Radarr/Sonarr.

## Utilisation

1. Page **Mojito** dans le menu principal (web) : recherche film/série,
   sélection d'épisode, choix de la release (triée selon le profil).
2. La session apparaît dans le canal **Mojito** de Jellyfin : lecture depuis
   n'importe quel client (web, Android TV...) dès que le tampon est prêt.
3. Gestion : page Mojito (sessions, terminer), ou directement dans qBittorrent
   en filtrant par le tag configuré (défaut : `mojito`).

## Transcodage

La lecture passe toujours par le **transcodage serveur (ffmpeg)** : la source
média du canal interdit le direct play et le direct stream (`SupportsDirectPlay`
et `SupportsDirectStream` à faux, `SupportsTranscoding` à vrai) et demande le
profil de transcodage le plus compatible (`UseMostCompatibleTranscodingProfile`).
ffmpeg tire le flux sur `/mojito/stream/{id}` et sort un format lisible par
tous les clients (H.264/AAC selon le profil du client). Les drapeaux
`IgnoreDts`/`IgnoreIndex`/`GenPtsInput` rendent le flux en cours de
téléchargement digeste pour ffmpeg.

## Limitations connues

- Seules les releases magnet sont streamables pour l'instant.
- MP4 avec moov en fin de fichier : lecture difficile tant que la fin du
  fichier n'est pas arrivée (atténué par la priorité première/dernière pièce ;
  le scorer pénalise les conteneurs non-MKV).
- Swarm plus lent que le bitrate : la lecture s'interrompt et reprend quand le
  préfixe progresse.
- Seek au-delà de la zone téléchargée non supporté (phase 3 du design).
- Le endpoint de streaming est anonyme (récupéré par le transcodeur serveur,
  sans token utilisateur) : le GUID de session sert de jeton non devinable.

## Avertissement

Ce plugin orchestre des outils de l'utilisateur (Radarr, Sonarr, qBittorrent)
et n'embarque aucune source de contenu. L'usage reste sous la responsabilité de
l'utilisateur : téléchargez et partagez uniquement des contenus que vous avez
le droit de posséder.
