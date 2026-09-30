# Modèle de décision en mode ombre

Le planificateur stratégique reste `glm-5.3-flash`. On peut lui adjoindre, **uniquement en mode ombre**, un modèle de décision typée local : Nimble de Bespoke Labs, servi par Ollama ≥ 0.35.0 via `POST /v1/systemone`. Ce modèle ne génère pas de texte et n'appelle pas d'outils. Il classe des options fournies par C# et renvoie une probabilité pour chacune.

## Rôle et limites de conception

- **Options.** C# calcule toutes les options à partir des faits journalisés, 26 au plus :
  - recherches disponibles, celles du chemin du silo en premier ;
  - automatisation des packs de science ;
  - défense ;
  - « aucune ».

  Le modèle ne produit ni position, ni orientation, ni objectif libre.
- **Deux questions par décision de GLM :**
  - `next_goal` : quel serait le meilleur prochain objectif parmi les options ;
  - `proposal_sound` : la proposition de GLM est-elle saine (sûre, utile vers la fusée, pas une répétition d'échec).
- **Journalisation.** Le verdict est écrit dans le journal de l'objectif (`decision-shadow`). Il n'atteint jamais un exécuteur, et un échec du modèle n'a aucun effet sur la partie. La défense continue d'être exécutée pendant l'appel.
- **Configuration.** Section `DecisionModel` distincte du profil local, désactivée par défaut, avec `Mode=shadow` obligatoire et un serveur local uniquement. Aucun basculement automatique entre modèles ou transports.
- **Licence.** Le tag Ollama `nimble:9b-q4_K_M` fusionne le LoRA Apache-2.0 avec Qwen3.5-9B (Apache-2.0). Nimble-V3 (CC BY-NC) est exclu.

## Installation constatée (30/09/2026)

| Élément | Valeur |
|---|---|
| Ollama | 0.35.0 via `winget install Ollama.Ollama` |
| Modèle | `nimble:9b-q4_K_M`, digest `572f1f4c801d`, 5,6 Go |
| GPU | RTX 3090, environ 5,6 Go de VRAM utilisés |
| Latence | 116 s à froid (chargement), 0,26 à 0,9 s à chaud |
| Format `noul` observé | Probabilité de « oui » dans `noul`, sans dictionnaire de probabilités |

`run-goal` et `run-campaign` préchauffent le modèle en parallèle. Le premier appel peut donc expirer sans conséquence.

## Premières mesures hors ligne (`decision-replay`)

| Journaux rejoués | Décisions | Accord avec GLM* | Latence p50 | Observations |
|---|---|---|---|---|
| Monde de développement (12/09) | 53, dont 51 évaluées | 70 % (28/40) | 0,86 s | Désaccords surtout vers des recherches hors chemin du silo (`lamp`, `radar`, `military` à la place de `steel-processing` ou `electric-energy-distribution-1`). |
| Campagne du 30/09 (graine 20260930) | 8 | 33 % (1/3) | 0,73 s | Choisit « aucune » pour les petits lots de plaques. |

\* Accord mesuré seulement lorsque la proposition de GLM figurait parmi les options de C#. Un accord n'est pas une preuve d'exactitude.

La question `proposal_sound` est peu discriminante : 47 propositions sur 59 jugées saines. Les refus portent surtout sur des déploiements répétés de tourelles.

**Conclusion provisoire** : utile comme second avis consigné, mais pas meilleur que GLM pour choisir. L'usage reste consultatif. Tout usage décisionnel exigerait de battre nettement une heuristique C# sur un jeu étiqueté par l'utilisateur, sans dégrader la survie.

## Commandes

```powershell
dotnet $hostDll decision-replay --session $sessionFile --quantity 200
```

Avec `DecisionModel.Enabled=true` dans `config/appsettings.local.json`, `run-campaign` journalise en plus un verdict `decision-shadow` après chaque proposition.
