# Modèle de décision en mode ombre

Le planificateur stratégique reste `glm-5.3-flash`. On peut lui adjoindre, **uniquement en mode ombre**, un modèle de décision typée local : Nimble de Bespoke Labs, servi par Ollama ≥ 0.35.0 via `POST /v1/systemone`. Ce modèle ne génère pas de texte et n'appelle pas d'outils. Il classe des options fournies par C# et renvoie une probabilité pour chacune.

## Rôle et limites de conception

- **Options.** C# calcule toutes les options à partir des faits journalisés, 26 au plus :
  - recherches disponibles, celles du chemin du silo en premier ;
  - automatisation des packs de science pas encore automatisés, et relèvement de l'approvisionnement des matières brutes (4 au plus) ;
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

## Mesures pendant la campagne headless du 2 octobre 2026

Sur la partie normale de graine 20261017, sept verdicts Nimble ont été journalisés, toujours en ombre : cinq concordent avec la proposition GPT, deux divergent. Les durées mesurées vont de 2,241 à 16,866 secondes pour Nimble et de 5,447 à 12,762 secondes pour GPT-6.1 Sol. Nimble n'est donc pas constamment plus rapide dans cet échantillon sur ce poste. Ces sept observations ne suffisent pas à établir la qualité relative des décisions, et aucun verdict Nimble n'a atteint l'exécuteur. Le [bilan du run](factory.md#campagne-normale-headless-du-2-octobre-2026) distingue les recherches acquises de l'usine encore inachevée ; les journaux restent privés.

## d3 et refonte du prompt (31 octobre 2026)

Le modèle de décision [d3](https://ollama.com/library/d3) (vLLM Semantic Router, Apache-2.0) utilise le même point d'accès `/v1/systemone` que Nimble : il se sélectionne par `DecisionModel.Model` (`d3:4b` ou `d3:9b`, Ollama ≥ 0.40.9), sans changement de code. Ses paquets Ollama sont limités à 4 096 tokens de contexte.

Un premier rejeu a montré que la question construite par C# égarait le modèle : automatisation proposée alors qu'elle existait déjà, option « aucune » trop attirante, recherches décrites par leur seul coût, et consigne de défense contraire à celle du planificateur (« défendre si l'industrie est exposée », alors que des points non couverts existent presque toujours). La question a été refaite :

- une automatisation déjà en place n'est plus proposée, sauf si le planificateur la vise (« augmenter ») ; l'approvisionnement des matières brutes devient une option ;
- chaque recherche nomme ce qu'elle débloque ;
- « aucune » ne s'applique que si toutes les options sont impossibles ou nuisibles ;
- priorités : menace réelle (ennemis visibles ou attaques récentes ; l'industrie non couverte seule n'en est pas une), puis blocages, puis chemin du silo, puis automatisation ;
- l'état résume les blocages, le résultat précédent et les cellules, et reste sous 3 000 caractères.

Rejeu hors ligne des 15 décisions des campagnes du 31 octobre (graine 20261031 ; Codex puis modèle local), mêmes journaux avant et après :

| Modèle | Prompt | Accord avec le planificateur* | « aucune » | Entrée max | Latence p50 |
|---|---|---|---|---|---|
| `d3:4b` | avant | 4/12 | 2 | 3 543 tokens | 816 ms |
| `d3:4b` | après | 8/13 | 0 | 1 422 tokens | 395 ms |
| `d3:9b` | avant | 0/12 (`gun-turret` choisi 12 fois sur 15, sans ennemi) | 2 | 3 543 tokens | 1 538 ms |
| `d3:9b` | après | 9/13 | 0 | 1 422 tokens | 717 ms |

\* L'accord n'est pas une preuve d'exactitude. Après correction, les désaccords portent surtout sur les décisions plus tardives (`logistics` ou une matière brute préférées à `steel-processing`, qui avait pourtant réussi). La question `proposal_sound` reste peu discriminante (14 ou 15 propositions jugées saines sur 15). L'usage reste exclusivement en ombre ; 15 décisions ne suffisent pas à établir une qualité de décision.

## Commandes

```powershell
dotnet $hostDll decision-replay --session $sessionFile --quantity 200
```

Avec `DecisionModel.Enabled=true` dans `config/appsettings.local.json`, `run-campaign` journalise en plus un verdict `decision-shadow` après chaque proposition.
