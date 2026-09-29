# Design — Jellyfin Mojito

**Jellyfin Mojito** : plugin Jellyfin qui résout un film/série via
Sonarr/Radarr, sélectionne la release correspondant au quality profile
configuré, pousse le torrent vers qBittorrent en téléchargement séquentiel
(taggé pour gestion dédiée), et démarre la lecture dans Jellyfin pendant le
téléchargement.

> v2 — passe de Prowlarr à Sonarr/Radarr comme source de sélection ;
> stratégie de tags qBittorrent explicite ; montage commun confirmé.

## 1. Objectif et flux cible

```
Utilisateur (client Jellyfin : web, TV, mobile)
      │  recherche "Dune 2"
      ▼
Mojito (channel Jellyfin / page plugin)
      │  Radarr: GET /api/v3/movie/lookup?term=Dune 2        → résolution TMDB
      │  Radarr: POST /api/v3/movie { monitored=false }      → création silencieuse
      │  Radarr: GET /api/v3/release?movieId=…                → releases indexers
      │  Radarr: GET /api/v3/qualityprofile/{id}              → critères du profil
      ▼
Mojito : filtre + score les releases (qualités autorisées, custom formats,
         seeders) → propose la meilleure + alternatives
      │
      ▼
Mojito ── POST /api/v2/torrents/add (magnet de la release choisie)
         sequentialDownload=true, firstLastPiecePrio=true,
         category="mojito", tags="mojito,mojito-{sessionId}"
      │
      ▼
qBittorrent télécharge séquentiellement ── le torrent est identifiable
      │                                    et gérable par tag (§3.4)
      ▼
Bridge de streaming de Mojito : sert http://<jellyfin>/plugin/mojito/stream/{id}
en lisant le préfixe déjà téléchargé du fichier sur disque (mount commun
Jellyfin/qBittorrent — confirmé)
      │
      ▼
Jellyfin lit le flux (remux ffmpeg) ──► lecture "en direct"
```

## 2. Faits techniques vérifiés (sept. 2026)

| Sujet | Fait |
|---|---|
| Jellyfin 12.x | Serveur en .NET 10 ; `Jellyfin.Controller`/`Jellyfin.Model` 12.x ciblent `net10.0` ; manifest `targetAbi` `12.0.0.0`. Plugins 10.11 à reconstruire. |
| Recherche extensible | Jellyfin 12 permet à un plugin d'injecter ses résultats dans la recherche globale du serveur. |
| Radarr v3 API | `GET /api/v3/movie/lookup?term=` (résolution TMDB, hors bibliothèque) ; `POST /api/v3/movie` (ajout, `monitored=false` possible) ; `GET /api/v3/release?movieId=` (recherche interactive sur les indexers, **nécessite le film en bibliothèque**) ; `GET /api/v3/qualityprofile` (qualités autorisées, ordre) ; `DELETE /api/v3/movie/{id}`. Auth : `X-Api-Key`. |
| Sonarr v3 API | `GET /api/v3/series/lookup?term=` ; `POST /api/v3/series` ; `GET /api/v3/episode?seriesId=` ; `GET /api/v3/release?episodeId=` (recherche interactive) ; profiles qualité identiques à Radarr. |
| qBittorrent Web API v2 | `POST /api/v2/torrents/add` (`urls`, `savePath`, `category`, `tags`, `sequentialDownload`, `firstLastPiecePrio`, `paused`) ; `GET /api/v2/torrents/info?tag=…&category=…` ; `POST /api/v2/torrents/addTags` / `removeTags` ; `POST /api/v2/torrents/pause` / `resume` / `delete` ; `GET /api/v2/torrents/properties` (progress, dlspeed) ; `GET /api/v2/torrents/files` (progress par fichier) ; `POST /api/v2/torrents/filePrio`. |
| Auth qBittorrent | `POST /api/v2/auth/login` (cookie SID) ou bypass localhost. |

## 3. Décisions d'architecture

### 3.1 Forme du plugin

Un seul plugin C# (`Jellyfin.Plugin.Mojito`, `net10.0`, ABI 12.0.0.0), nom
utilisateur-facing « **Jellyfin Mojito** », sans processus externe. Les
"*arr" et qBittorrent existent déjà ; Mojito fait de la résolution, de la
sélection et de l'orchestration.

```
Jellyfin.Plugin.Mojito
├── MojitoPlugin                   (IPlugin, configuration)
├── Configuration
│   ├── PluginConfiguration        (Radarr/Sonarr URL+clé, qBittorrent URL+creds,
│   │                              tags, savePath, buffer, nettoyage)
│   └── PluginConfigPage           (page d'admin)
├── Providers                       ── abstraction IReleaseProvider
│   ├── RadarrProvider              (lookup movie, releases, profiles)
│   ├── SonarrProvider              (lookup series, episodes, releases, profiles)
│   └── ProwlarrProvider            (optionnel : repli recherche brute)
├── Selection
│   └── ReleaseScorer               (filtre par quality profile, score
│                                   seeders/custom formats/taille)
├── Channel
│   ├── MojitoChannel              (IChannelProvider : recherche + navigation,
│   │                              tous les clients)
│   └── MediaSourceProvider         (IMediaSourceProvider : flux du channel item)
├── Streaming
│   ├── StreamSessions              (registre sessionId → torrentHash, fichier,
│   │                                état, offsets)
│   ├── StreamBridgeController      (GET stream/{id} : chunked, préfixe lu)
│   └── SessionOrchestrator          (IHostedService : sessions, surveillance
│                                    qBittorrent, nettoyage, réconciliation)
└── WebUI (wwwroot)                 (recherche, choix release, état streams)
```

### 3.2 Deux modes de récupération (config)

| Mode | Principe | Quand |
|---|---|---|
| **Assistée** (défaut) | Mojito interroge `/api/v3/release`, filtre/score selon le quality profile, et pousse lui-même le magnet à qBittorrent avec ses tags et sa catégorie. Rien n'est importé : Radarr/Sonarr ne voient pas le téléchargement → pas de renommage, pas de déplacement, streaming sûr. | Lecture éphémère "à la demande" |
| **Grab \*arr** (option) | Mojito ajoute le média `monitored=true` avec le profil choisi et déclenche la recherche standard (`MoviesSearch`/`SeriesSearch`). Radarr/Sonarr gèrent tout (sélection 100 % fidèle au profil, renommage, import en bibliothèque finale). Mojito récupère le hash dans la queue (`/api/v3/queue`), re-tague le torrent `mojito` + `mojito-{sessionId}` pour le suivre et streamer. | On veut aussi enrichir sa bibliothèque |

En mode Assistée, le média créé en bibliothèque *arr est `monitored=false`
et supprimé (`DELETE /api/v3/movie/{id}`) à la fin de la session, sauf si
l'utilisateur coche "conserver dans la bibliothèque" (utile pour
téléchargement différé ultérieur).

Note de fidélité : en mode Assistée, la sélection ne réplique pas la totalité
de la logique de scoring de Radarr (preferred words custom complexes). Le
scorer utilise ce que l'API expose : qualités autorisées du profil, score
custom format quand présent dans la réponse release, seeders/leechers, taille.
Le mode Grab *arr reste la référence si le profil est très personnalisé.

### 3.3 Stratégie de buffering (lecture "en direct")

qBittorrent reçoit `sequentialDownload=true` + `firstLastPiecePrio=true` :
le fichier possède un **préfixe complet croissant** à la vitesse de
téléchargement, et la fin du fichier arrive tôt (probe des métadonnées
fin de conteneur par ffmpeg).

```
maxReadableOffset(t) = progress(fichier cible) × taille − margeSecurité
```

1. **Démarrage** : lecture quand `progress ≥ seuil` (3 % ou 30 Mo) ET
   `dlspeed` compatible avec le bitrate. Sinon : état "mise en tampon"
   (vitesse, ETA) dans l'UI.
2. **Débit** : readahead `min(dlspeed × 10 s, 32 Mo)` ; le bridge endort la
   réponse chunked quand le pointeur atteint `maxReadableOffset` (timeout +
   déconnexion propre si le swarm meurt).
3. **Seek** : autorisé dans `[0, maxReadableOffset]` ; au-delà : phase 2 =
   refus propre avec message, phase 3 = priorisation des pièces autour de
   l'offset.
4. **Fin** : sur fin de session de lecture (`ISessionManager`) ou
   inactivité, application de la politique de nettoyage (§3.4).

### 3.4 Tags et gestion des torrents qBittorrent

Chaque torrent lancé par Mojito est taggé et catégorisé de façon stable et
configurable :

| Marqueur | Valeur défaut | Usage |
|---|---|---|
| Tag global | `mojito` | Filtre qBittorrent natif : l'utilisateur voit et gère tous les streams de Mojito d'un coup (`torrents/info?tag=mojito`) |
| Tag de session | `mojito-{sessionId}` | Suivi individuel, corrélation session ↔ torrent (survit aux redémarrages) |
| Catégorie | `mojito` | Isole des téléchargements *arr habituels ; permet un `savePath` différent via les catégories qBittorrent |
| Étiquette état | `mojito-state:{pending\|buffering\|playing\|ended\|failed}` | État visible directement dans la colonne Tags de qBittorrent |

Opérations de gestion (page plugin + API) :

- `GET /api/v2/torrents/info?tag=mojito` → inventaire temps réel.
- Pause/reprise/stop/suppression (avec ou sans fichiers) d'une session ou
  de toutes les sessions — appels qBittorrent ciblés par tag de session.
- `POST /api/v2/torrents/setTags` pour refléter les transitions d'état.
- **Réconciliation au démarrage du plugin** : tout torrent `mojito` sans
  session active (redémarrage Jellyfin, crash) reçoit `mojito-state:ended`
  et la politique de nettoyage par défaut s'applique (pause, sauf `seed`).
- Les réglages catégorie/tag/suffixes sont configurables pour cohabiter
  avec un tagging existant chez l'utilisateur.

### 3.5 Sélection du fichier dans un torrent multi-fichiers

- Filtrage des extensions vidéo (`mkv`, `mp4`, `avi`, …).
- Saisons/packs : correspondance SxxExx du nom de fichier contre l'épisode
  visé (la recherche Sonarr étant par `episodeId`, la release est déjà
  ciblée épisode ; utile pour les packs multi-épisodes).
- Autres fichiers → priorité 0 (`torrents/filePrio`).

### 3.6 Interface utilisateur

- **Channel Jellyfin** (`IChannelProvider`) : vitrine "liste de médias" et
  recherche, sur tous les clients.
- **Page plugin** (web) : recherche (film ou série), sélection de
  l'épisode (Sonarr), liste des releases scorées (qualité, profil,
  seeders, verdict "streamable"), gestion des sessions (progression,
  vitesse, état, stop/nettoyage), option "conserver en bibliothèque *arr".
- **Recherche globale Jellyfin 12** : injection des résultats (phase 3).

## 4. Endpoints du plugin

| Méthode/Route | Rôle |
|---|---|
| `GET /plugin/mojito/lookup?type=movie\|series&query=` | Résolution TMDB via Radarr/Sonarr (pas de recherche raw) |
| `GET /plugin/mojito/episodes?seriesId=` | Épisodes d'une série (Sonarr) |
| `GET /plugin/mojito/releases?type=&mediaId=&episodeId=` | Releases scorées selon le quality profile (mode Assistée) |
| `POST /plugin/mojito/play` | Corps : `{ mediaId, episodeId?, releaseGuid?, mode: assisted\|grab, keepInLibrary? }` → session + itemId |
| `GET /plugin/mojito/sessions` | Sessions actives (progression, vitesse, état, hash, tags) |
| `POST /plugin/mojito/sessions/{id}/{pause\|resume\|stop}` | Gestion ciblée d'une session |
| `DELETE /plugin/mojito/sessions/{id}` | Stop + politique de nettoyage |
| `GET /plugin/mojito/stream/{id}` | **Flux vidéo** (chunked, MIME, Range sur le préfixe) |

Le channel expose chaque session comme `ChannelItem` vidéo dont le
`MediaSource` (`IMediaSourceProvider`) pointe vers `stream/{id}`,
`Protocol=Http`, `SupportsDirectPlay=false`, `SupportsDirectStream=true`
→ remux/transcode serveur, compatible tous les clients.

## 5. Modèle de données (en mémoire + JSON persisté)

```csharp
class StreamSession {
    Guid Id;
    MediaKind Kind;          // Movie, Episode
    string MediaId; string Title; string? EpisodeInfo;
    string ReleaseGuid; string TorrentHash; string MagnetUri;
    string ProviderTag;      // mojito-{sessionId}
    string SavePath; string MediaFileName; long MediaFileSize;
    string ItemId;           // channel item Jellyfin
    SessionState State;      // Pending, Buffering, Playing, Ended, Failed
    long MaxReadableOffset; double Progress; long DlSpeed;
    bool KeepInLibrary;      // mode Assistée : conserver le média *arr
    DateTime CreatedAt;
}
```

## 6. Configuration (page admin)

| Clé | Défaut | Note |
|---|---|---|
| `RadarrUrl` / `RadarrApiKey` | `http://localhost:7878` | `X-Api-Key` |
| `SonarrUrl` / `SonarrApiKey` | `http://localhost:8989` | `X-Api-Key` |
| `ProwlarrUrl` / `ProwlarrApiKey` (optionnel) | `http://localhost:9696` | repli recherche brute |
| `QbitUrl`, `QbitUsername/Password` | `http://localhost:8080` | ou bypass localhost |
| `SavePath` | — | mount commun Jellyfin/qBittorrent (confirmé) |
| `MovieQualityProfile` / `SeriesQualityProfile` | profil défaut *arr | sélection dans la page admin |
| `DefaultMode` | `assisted` | ou `grab` |
| `TorrentTag` | `mojito` | configurable |
| `TorrentCategory` | `mojito` | configurable |
| `StartThresholdBytes` / `StartThresholdPercent` | 30 Mo / 3 % | |
| `CleanupPolicy` | `pause` | `pause` / `remove` / `seed` |

## 7. Limitations et risques assumés

1. **Création silencieuse en bibliothèque \*arr** (mode Assistée) : brève et
   non-monitorée ; `monitored=false` = pas de grab/import automatique.
   Suppression à la fin de session par défaut. Risque : timestamp "added"
   modifié pour un média déjà présent — Mojito détecte "déjà en
   bibliothèque" et ne touche à rien dans ce cas.
2. **Conteneurs non streamables** : MP4 non-faststart. `firstLastPiecePrio`
   atténue ; le scorer dépriorise les MP4 si un MKV équivalent existe.
3. **Swarm lent** : `dlspeed < bitrate` → interruptions gérées (pont endort
   le flux, verdict "streamable" affiché avant lancement).
4. **Seek hors zone** : phase 2 refus propre ; phase 3 priorisation.
5. **Mode Grab \*arr** : le déplacement/import en fin de téléchargement
   peut couper une lecture encore en cours — Mojito bloque le nettoyage
   tant qu'une session est active, et l'import final est attendu avant
   suppression du suivi.
6. **Fidélité du scoring** (mode Assistée) : voir §3.2 — Grab *arr = vérité.
7. **Légalité** : le plugin orchestre les outils de l'utilisateur ; aucune
   source de contenu embarquée.

## 8. Plan de livraison

| Phase | Contenu | Critère d'acceptation |
|---|---|---|
| M0 | Scaffold plugin `Jellyfin.Plugin.Mojito` (`net10.0`, ABI 12.0.0.0), manifest | `dotnet build` OK, plugin chargé dans Jellyfin 12.x |
| M1 | Config page + `RadarrProvider` + lookup + releases scorées | Recherche "Dune 2" → liste releases avec qualités du profil |
| M2 | `QbitClient` + tags + session + channel + bridge streaming | Recherche → sélection → lecture pendant téléchargement (client web), torrent visible `mojito` dans qBittorrent |
| M3 | `SonarrProvider` (épisodes), buffering adaptatif, seek, nettoyage, réconciliation par tag | Lecture Android TV, gestion/pause/suppression des sessions |
| M4 | Mode Grab \*arr, recherche globale JF12, packaging repo | Manifest installable, README prérequis |

## 9. Arborescence projet

```
jellyfin-plugin-mojito/
├── DESIGN.md
├── README.md                (M4)
├── Jellyfin.Plugin.Mojito/
│   ├── Jellyfin.Plugin.Mojito.csproj
│   ├── MojitoPlugin.cs
│   ├── Configuration/…
│   ├── Providers/…
│   ├── Selection/…
│   ├── Channel/…
│   ├── Streaming/…
│   └── WebUI/…
├── Mojito.Tests/
└── build.yaml / manifest.json
```
