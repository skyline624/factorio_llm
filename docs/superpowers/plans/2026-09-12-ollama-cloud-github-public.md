# Ollama Cloud et publication GitHub

Statut : implémentation, essais et publication autorisés selon le [plan d'exécution](../../execution-plan.md). Ce document remplace la préparation antérieure en phase de planification. La publication effective et chaque essai restent à constater séparément.

## Profil retenu

Le contrôleur C# contacte la passerelle Ollama, par défaut `http://localhost:11434`, qui relaie l'inférence vers Ollama Cloud. Modèle exact : `glm-5.3-flash:cloud`. Aucun remplacement automatique de modèle ou de fournisseur.

Le [profil exemple](../../../config/appsettings.example.json) définit la configuration de l'application : modèle, adresse, réponse sans streaming et effort de raisonnement `low`. Ce document JSON n'est pas le corps d'une requête HTTP à Ollama. Les propriétés doivent être lues et traduites par l'adaptateur ; leur compatibilité réelle est qualifiée lors de l'intégration. Ne pas envoyer `think: false` pour ce profil.

Les appels d'outils proposent des objectifs libres ; ils ne sont pas des commandes Factorio. C# contrôle les arguments, traduit les objectifs en critères mesurables et synthétise plans, placements et routes sans gabarits prédéfinis. L'exécuteur Lua revérifie les préconditions et rapporte les effets constatés.

La sortie contrainte par JSON Schema n'est pas présumée disponible dans le cloud. Une requête invalide, tronquée, périmée ou contenant des paramètres inattendus ne produit aucune nouvelle opération. Les plans encore valides et la défense continuent pendant les erreurs d'authentification, quotas ou pannes du modèle.

## Contenu public préparé

- [README](../../../README.md) français décrivant les objectifs, prérequis et limites de qualification.
- [Licence MIT](../../../LICENSE), copyright 2026 skyline624, pour le code original.
- [Contribution](../../../CONTRIBUTING.md), [instructions du projet](../../../AGENTS.md), [exclusions Git](../../../.gitignore) et configuration exemple sans secrets.
- [CI C#](../../../.github/workflows/ci.yml) sous Windows, avec le SDK de [global.json](../../../global.json).
- [Conception](../../design.md), [références](../../research.md), [feuille de route](../../implementation-plan.md) et plan d'exécution.

Les binaires, assets et documentation distribuée de Factorio, modèles, sauvegardes, bases d'état, journaux privés et secrets sont exclus. Les fixtures synthétiques documentées restent possibles. L'étude d'une référence ne signifie pas reprise de code ; toute incorporation doit conserver les conditions et notices applicables.

## Contrôle de la CI

La CI installe le SDK indiqué, vérifie la présence des projets dans `Factorio.Agent.sln`, puis restaure, compile et teste. Elle échoue si la solution est vide, si aucun rapport TRX n'est produit ou si aucun test n'a été exécuté. Elle ne télécharge pas le jeu, ne lance pas Factorio et n'appelle pas le cloud.

Les actions officielles sont épinglées aux commits vérifiés lors de la préparation :

| Action | Version vérifiée | Commit |
|---|---|---|
| [actions/checkout](https://github.com/actions/checkout/releases/tag/v7.0.1) | v7.0.1 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| [actions/setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0) | v6.0.0 | `a98b56852c35b8e3190ac28c8c2271da59106c68` |

Les essais Factorio et campagnes cloud sont déclenchés séparément sur un poste configuré. Leur résultat ne se déduit pas du statut de la CI.

## Ordre de publication

1. Achever et relire le contenu livrable, vérifier les projets compilés et exécuter les tests nécessaires.
2. Examiner la liste exacte des fichiers indexés et leur diff ; confirmer l'absence de jeu, données locales et secrets.
3. Préparer le premier commit publiable et la branche principale `main`.
4. Recontrôler le compte GitHub et l'état de `skyline624/factorio_llm` avant sa création publique ; si le dépôt existe déjà, examiner son état sans écraser son contenu.
5. Publier uniquement le contenu relu, puis vérifier visibilité `PUBLIC`, branche par défaut, commit distant et résultat réel de la CI.
6. Poursuivre sur des branches `codex/...` avec pull requests.

Le compte `skyline624` et l'absence de dépôt distant avaient été vérifiés pendant la préparation initiale. Ces observations historiques ne remplacent pas le contrôle précédant la publication. Aucun élément de cette liste ne prouve un lancement autonome de fusée.

## Qualification de l'adaptateur cloud

| Cas | Résultat exigé |
|---|---|
| Appel `POST /api/chat` | Modèle exact, réponse sans streaming et contrat d'outils réellement observé |
| JSON ou arguments invalides | Rejet motivé sans nouvelle opération moteur |
| Objectif libre | Traduction C# en critères mesurables et dépendances ; reformulation bornée si non compris |
| Authentification/modèle absent | Diagnostic distinct ; aucune substitution silencieuse |
| Quota, limite ou panne | Tentatives bornées ; respect de `Retry-After` quand présent ; plans valides et défense maintenus |
| Réponse tardive | Contrôle de fraîcheur avant acceptation ; aucune répétition de mutation |
| Contexte et mesures | Synthèse choisie par l'application ; latence, tentatives et tokens retournés consignés ; métriques absentes déclarées inconnues |

Une expiration locale n'atteste ni l'arrêt de l'inférence distante ni l'absence de consommation. Le journal complet, les secrets et la configuration privée du poste ne sont pas envoyés comme contexte. Une limite locale `num_ctx` n'est pas présentée comme une garantie du contexte cloud.

## Références

- [Modèle GLM-5.3-Flash](https://ollama.com/library/glm-5.3-flash:cloud), [passerelle cloud](https://docs.ollama.com/cloud).
- [Sorties structurées](https://docs.ollama.com/capabilities/structured-outputs), [appels d'outils](https://docs.ollama.com/capabilities/tool-calling), [erreurs](https://docs.ollama.com/api/errors).
- [Création de dépôt avec GitHub CLI](https://cli.github.com/manual/gh_repo_create).
