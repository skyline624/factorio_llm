# Contribuer

Lire le [plan d'exécution](docs/execution-plan.md), la [conception](docs/design.md) et les [instructions du projet](AGENTS.md) avant de modifier un contrat. Le développement est actif ; une exigence décrite n'est pas nécessairement déjà implémentée ou qualifiée.

## Changements

Utiliser une branche `codex/...` et une pull request limitée à un problème. Décrire le comportement attendu, le changement, les contrôles réellement effectués et les limites restantes. Appliquer SOLID, DRY et KISS : responsabilités courtes, interfaces aux frontières et abstractions motivées par un usage concret.

Le LLM propose des objectifs libres. C# décide des plans, des placements et du routage ; Lua applique les opérations natives avec validation et preuve. Les critères d'exécution doivent distinguer acceptation, progression, effet partiel, interruption et résultat inconnu après incident réseau. Ne pas transformer une suggestion de modèle en commande moteur.

## Vérifier

Installer le SDK de [global.json](global.json), puis exécuter depuis la racine :

```powershell
dotnet restore Factorio.Agent.sln
dotnet build Factorio.Agent.sln --configuration Release --no-restore
dotnet test Factorio.Agent.sln --configuration Release --no-build
```

Lorsque RTK est disponible, préfixer les commandes locales par `rtk`. La CI utilise directement les outils du runner ; elle ne dépend d'aucun fichier personnel.

Les tests ordinaires doivent fonctionner sans installation de Factorio, authentification Ollama ou appel cloud. Les tests d'intégration réels sont séparés, explicitement déclenchés et exécutés avec une installation personnelle de Factorio 2.0.77. Documenter graine, version, scénario, conditions initiales, intervention éventuelle et critères observés. Une fixture artificielle ne peut pas être présentée comme une campagne autonome. Tout changement du chargement ou de la persistance doit également être vérifié avec sauvegarde/reprise et connexion d'un client.

## Contenu publiable

Ne pas committer jeu, assets du jeu, documentation livrée avec son installation, modèles, secrets, sauvegardes, bases d'état ni journaux privés. Placer les données d'exécution sous `.runtime/` et publier seulement des exemples sans secrets ou des fixtures synthétiques documentées. Les observations et historiques envoyés au modèle cloud doivent exclure les identifiants d'authentification et la configuration privée du poste.

Avant une publication, examiner la liste exacte des fichiers indexés et leur diff. Le `.gitignore` ne retire pas un fichier déjà suivi. Éviter les fichiers issus d'autres projets sans vérifier leur provenance et leurs conditions de redistribution ; conserver les notices nécessaires. Proposer tout ajout de dépendance avec sa licence et sa justification. Le projet original est sous [MIT](LICENSE), sans extension de cette licence aux composants tiers.
