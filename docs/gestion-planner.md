# Planificateur local Gestion

Le transport `gestion-local` est une sélection explicite, indépendante d'Ollama et de Codex. Il interroge le serveur LLM local « Gestion » par son API compatible OpenAI (`POST /v1/chat/completions`). Comme pour les autres transports, le modèle propose seulement un objectif par l'outil `propose_goal` : C# le valide et le décompose, et aucune opération de jeu ne vient du modèle. Aucun passage automatique vers un autre modèle ou un autre transport n'est prévu.

## Configuration

Copier `config/appsettings.gestion.example.json` vers un profil privé ignoré par Git, par exemple `config/appsettings.gestion.local.json`, avec `Planner.Transport=gestion-local` et une section `Gestion` :

- `BaseUrl` : URL `https` absolue, sans identifiants, paramètres ni fragment.
- `Model` : identifiant **concret** du modèle servi. L'alias `llm-actif` est refusé, car il suit silencieusement le moteur actif et ferait changer de modèle sans trace. La réponse doit nommer exactement ce modèle, sinon elle est rejetée.
- `ApiKey` : facultative dans le profil. À défaut, la variable d'environnement `GESTION_API_KEY` est lue ; une clé non vide du profil a priorité. La clé n'apparaît ni dans Git, ni dans les journaux, ni dans les messages d'erreur, ni en argument de ligne de commande.
- `CaCertificate` : fichier PEM de l'autorité privée. Les variables d'environnement sont développées ; un chemin relatif est résolu depuis le dossier du profil.
- `RequestTimeoutSeconds` (180 par défaut, 10 minutes au plus) et `MaxTokens` (4096 par défaut, de 256 à 32768).

## TLS

Le certificat du serveur est émis par une autorité privée et valable pour l'adresse IP. L'hôte ne désactive jamais la vérification : la chaîne est validée contre la seule autorité du fichier `CaCertificate`, et toute erreur autre qu'une chaîne non approuvée (nom ne correspondant pas, certificat absent) refuse la connexion. Cette autorité ne publie pas de liste de révocation : la révocation n'est donc pas vérifiable, comme avec Node et l'option `ca`.

## Vérification et campagne

```powershell
$env:GESTION_API_KEY = "<clé>"
dotnet run --project src/Factorio.Agent.Host -- check-gestion --config config/appsettings.gestion.local.json
dotnet run --project src/Factorio.Agent.Host -- run-campaign --session .runtime/<campagne>/session.json --config config/appsettings.gestion.local.json --minutes 10 --max-goals 5
```

`check-gestion` envoie un contexte synthétique annoncé comme tel, ne crée pas de monde et n'exécute aucune opération. Ne pas combiner `--ollama-cloud` avec ce profil.

Premier appel réel (2026-10-31) : `check-gestion` a obtenu une proposition validée de `huihui-glm-5.3-flash-ud-iq2-xxs` en 16 s pour 846 tokens d'entrée, certificat vérifié contre l'autorité privée. La cible proposée (`iron plates`) n'était pas un identifiant natif : en campagne, la validation C# la renverrait au modèle pour correction. Aucune campagne n'a encore été menée avec ce transport.

## Budgets et limites

Un seul essai par proposition, sans réessai interne : le contrôleur de campagne attend et réessaie déjà les erreurs transitoires (quota, réseau, délai). Les messages d'erreur ne contiennent jamais le corps de la réponse du serveur. La réponse est limitée à 2 MiB, les champs JSON dupliqués sont rejetés, et les arguments de l'outil (une chaîne JSON dans le format OpenAI) subissent la même validation C# que pour les autres transports.

Un modèle fortement quantifié peut produire davantage d'objectifs invalides ou tronqués (`finish_reason=length`) ; ils sont rejetés sans substitution. La validation du transport est distincte de la progression en jeu et de la qualification finale jusqu'à la fusée.
