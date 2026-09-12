# Feuille de route — implémentation active

L'implémentation, les essais réels et la publication sont autorisés. Le [plan d'exécution](execution-plan.md) définit les décisions actuelles et prévaut sur les propositions antérieures. Les jalons ci-dessous décrivent des critères à vérifier, sans annoncer leur achèvement.

Objectif : agent C# utilisant `glm-5.3-flash:cloud` via Ollama, avec mod Lua pour Factorio 2.0.77 de base, jusqu'au lancement d'une fusée avec attaques, pollution, évolution et expansion actives.

| Jalon | Livrable | Critère d'acceptation |
|---|---|---|
| 1. Socle public et contrats | README français, MIT, CI C#, contrat versionné et dépôt public `skyline624/factorio_llm` | Projets réellement reliés à la solution, compilation et tests effectifs ; contenu indexé relu ; visibilité, commit distant et résultat CI vérifiés |
| 2. Acteur et opérations natives | Avatar autonome headless, marche, minage, fabrication, construction, transferts, tir, registre d'opérations | Coûts et effets prouvés ; interruptions, mort et sauvegarde/reprise ; pilote rattaché au même avatar, bouton IA/Manuel et déconnexion sans duplication |
| 3. État et survie | Stocks de l'usine connue, fluides, transit, capacités, perception ennemie et arbitre prioritaire | Pas de doubles comptes ni visibilité cachée ; chemins bloqués et attaques traités pendant inférence lente ; réapparition normale et récupération dans le même monde |
| 4. Production et synthèse spatiale | Bilans de recettes et ressources, solveur d'implantation et routage C# | Aucune coordonnée LLM ni gabarit prédéfini ; solutions/délais/impossibilités distingués ; chaînes tapis/tuyaux fonctionnelles et débits soutenus |
| 5. Stratégie et progression | Objectifs libres, traduction en plans, mémoire, adaptateur cloud et dépendances scientifiques | Arguments invalides inopérants ; contrat réel du modèle qualifié ; plans valides et défense maintenus si Ollama manque ; chaîne complète jusqu'à la fusée |
| 6. Campagnes | Trois graines documentées avec départ normal et ennemis actifs | Lancements constatés, historique depuis le départ, absence de ressource artificielle et d'assistance humaine ; erreurs, décès, latences et consommation consignés |

La V1 emploie tapis et tuyaux ; trains et logistique robotique sont différés. Les priorités sont survie, défense, récupération puis production. Une mort n'autorise ni restauration pour effacer une défaite ni équipement gratuit. Les commandes manuelles d'un pilote marquent une campagne comme assistée.

Les contrôles de sauvegarde/reprise et du client graphique commencent dès le socle d'acteur, puis sont répétés après les changements pertinents. Les fréquences de télémétrie et les délais de défense sont déterminés par mesures. Les adaptations nécessaires au personnage sont qualifiées contre les règles normales avant toute preuve de campagne.

Une interface offrant des coordonnées au LLM, une solution vide ou une exécution réduite à un booléen ne satisfait pas ces critères. Une fixture injectant des ressources ou des ennemis isole un mécanisme ; elle ne démontre pas une progression normale. Seuls les résultats observés sont rapportés comme réussites.

Le [complément cloud/public](superpowers/plans/2026-09-12-ollama-cloud-github-public.md) précise configuration, CI et ordre de publication. Consulter aussi la [conception](design.md), les [sources](research.md) et les [instructions de contribution](../CONTRIBUTING.md).
