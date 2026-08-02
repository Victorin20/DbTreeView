# DB Tree View

DB Tree View est un MVP Blazor qui se connecte à une instance SQL Server,
découvre ses bases utilisateur accessibles et affiche leurs dépendances sous
forme de graphe interactif.

## Fonctionnalités disponibles

- Authentification Windows ou SQL Server.
- Test de la connexion avant l'analyse.
- Analyse de toutes les bases utilisateur en ligne et accessibles, ou d'une base
  unique au choix.
- Hiérarchie serveur → base → schéma → objet et filtre par base dans le graphe.
- Lecture en mémoire des objets utilisateur de chaque base.
- Dépendances issues de `sys.sql_expression_dependencies`.
- Résolution des dépendances entre deux bases analysées du même serveur.
- Relations de clés étrangères issues de `sys.foreign_keys`.
- Graphe Cytoscape.js avec zoom, recherche et plusieurs dispositions.
- Inspection des dépendances entrantes et sortantes d'un objet.
- Tableau de performances pour les procédures, tables et requêtes Query Store.
- Mesures CPU, durée, exécutions, lectures logiques, seeks, scans et écritures.
- Niveau de chaleur reporté sur les nœuds de la cartographie.
- Impact des vues estimé à partir des procédures qui les utilisent.
- Références externes ou non résolues affichées en gris.
- Mode démonstration ne nécessitant pas SQL Server.

Les identifiants ne sont enregistrés ni dans `appsettings.json`, ni dans un
fichier. Ils restent dans la session Blazor côté serveur et sont oubliés à la
fermeture de la session ou avec le bouton **Changer de serveur**.

## Prérequis

- Visual Studio avec la charge de travail **Développement ASP.NET et web**, ou
  le SDK .NET 10.
- Une instance SQL Server accessible depuis la machine qui exécute
  l'application.
- Un compte SQL disposant d'un accès aux bases à analyser et de la permission de
  consulter leurs métadonnées.

## Lancer le projet

Depuis Visual Studio, ouvrir `DbTreeView.slnx`, sélectionner `DbTreeView.Web`
comme projet de démarrage puis lancer le profil HTTPS.

En ligne de commande :

```powershell
dotnet restore DbTreeView.slnx
dotnet run --project DbTreeView.Web
```

Le formulaire propose `(localdb)\MSSQLLocalDB` par défaut. Remplacer ce nom par
le serveur réel, par exemple `SQL-PROD-01`, `localhost` ou
`SERVEUR\INSTANCE`.

L'option **Analyser tout le serveur** est activée par défaut. L'application se
connecte d'abord à `master`, interroge `sys.databases`, conserve les bases
utilisateur en ligne pour lesquelles `HAS_DBACCESS` vaut `1`, puis ouvre une
connexion en lecture seule vers chacune d'elles. Désactiver cette option permet
de saisir une base unique pour limiter le temps et la taille de l'analyse.

Avec l'authentification Windows, l'identité utilisée est celle du processus qui
exécute l'application. En développement depuis Visual Studio, il s'agit en
général de l'utilisateur Windows courant. Après un déploiement IIS, il faudra
configurer explicitement l'identité du pool d'applications ou utiliser un compte
SQL dédié en lecture seule.

## Permissions SQL Server

La cartographie ne lit aucune donnée métier et n'exécute aucune procédure. Elle
interroge uniquement le catalogue système de la base.

La permission suivante doit être appliquée dans chaque base à cartographier pour
rendre les définitions visibles :

```sql
USE [MaBase];
GRANT VIEW DEFINITION TO [MonUtilisateur];
```

Le compte doit également pouvoir se connecter à la base. Selon la politique de
permissions du serveur, l'administrateur peut devoir accorder l'accès en lecture
à `sys.sql_expression_dependencies`.

Ne pas donner `db_owner` à l'application uniquement pour contourner un problème
de permissions. Un compte dédié possédant les droits minimaux est préférable.

### Permissions pour les statistiques

La cartographie reste disponible sans droits de performance. L'application
affiche alors un avertissement pour chaque source inaccessible au lieu de faire
échouer toute l'analyse.

Pour SQL Server 2022 et les versions suivantes, l'administrateur peut accorder :

```sql
USE [master];
GRANT VIEW SERVER PERFORMANCE STATE TO [MonLogin];

USE [MaBase];
GRANT VIEW DATABASE PERFORMANCE STATE TO [MonUtilisateur];
```

Pour SQL Server 2019 et les versions précédentes, les permissions équivalentes
sont `VIEW SERVER STATE` et `VIEW DATABASE STATE` :

```sql
USE [master];
GRANT VIEW SERVER STATE TO [MonLogin];

USE [MaBase];
GRANT VIEW DATABASE STATE TO [MonUtilisateur];
```

Ces droits exposent des informations de diagnostic sur l'instance. Ils doivent
être accordés à un compte de supervision dédié et validés par l'administrateur
SQL Server.

## Comment fonctionnent les statistiques

- Les collecteurs sont exécutés séparément dans chaque base découverte, puis les
  résultats sont fusionnés au niveau du serveur.
- Les procédures proviennent de `sys.dm_exec_procedure_stats`. Les valeurs sont
  cumulées pour les plans actuellement présents dans le cache.
- L'activité des tables provient de `sys.dm_db_index_usage_stats`; le nombre de
  lignes et la taille réservée proviennent de `sys.dm_db_partition_stats`.
- Les requêtes les plus coûteuses proviennent de Query Store sur une fenêtre de
  sept jours, lorsqu'il est actif.
- Une vue classique n'a pas de temps d'exécution autonome. L'application parcourt
  les relations `DependsOn` et lui attribue l'impact des procédures qui
  l'atteignent. Cette mesure est explicitement marquée comme estimation.
- Les recommandations affichées sont des pistes de diagnostic basées sur des
  seuils. Elles ne remplacent pas l'analyse du plan d'exécution avant toute
  modification d'index ou de code SQL.

## Limites connues du MVP

- Les bases système (`master`, `model`, `msdb` et `tempdb`) sont exclues du mode
  serveur ; elles restent analysables avec le mode base unique.
- Les appels construits avec du SQL dynamique ne sont pas toujours détectables.
- Une référence inter-base est résolue lorsque la base cible fait partie du
  périmètre et que le schéma et l'objet sont identifiables. Une référence vers un
  autre serveur reste affichée comme objet externe.
- Sur un serveur contenant beaucoup de bases ou d'objets, l'analyse peut prendre
  du temps ; le filtre de base ou le mode base unique permettent de réduire le
  graphe affiché.
- Les compteurs de procédures disparaissent lorsqu'un plan quitte le cache ; les
  compteurs d'utilisation d'index peuvent repartir de zéro après un redémarrage.
- Query Store n'est jamais activé ou modifié automatiquement par l'application.
- Cette version affiche les pistes prioritaires, mais pas encore les plans
  d'exécution graphiques ni les attentes SQL Server.

## Organisation

```text
DbTreeView.Web/
├── Components/       interface Blazor et tableau de performances
├── Models/           objets, relations et mesures
├── Services/         session, métadonnées et collecteurs SQL Server
└── wwwroot/          styles, Cytoscape.js et intégration du graphe
```

Les requêtes de cartographie se trouvent dans
`Services/SqlServerMetadataService.cs`; les requêtes de performance et le calcul
d'impact se trouvent dans `Services/SqlServerPerformanceService.cs`. Le rendu
interactif se trouve dans `wwwroot/graph.js`.
