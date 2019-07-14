/*
 * MindTouch λ#
 * Copyright (C) 2018-2019 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using LambdaSharp.Tool.Internal;
using LambdaSharp.Tool.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LambdaSharp.Tool.Cli.Publish {

    public class PublishStep : AModelProcessor {

        //--- Constants ---
        private const string AMAZON_METADATA_ORIGIN = "x-amz-meta-lambdasharp-origin";

        //--- Fields ---
        private readonly ModelManifestLoader _loader;
        private readonly TransferUtility _transferUtility;
        private bool _changesDetected;
        private bool _forcePublish;

        //--- Constructors ---
        public PublishStep(Settings settings, string sourceFilename) : base(settings, sourceFilename) {
            _loader = new ModelManifestLoader(Settings, "cloudformation.json");
            _transferUtility = new TransferUtility(Settings.S3Client);
        }

        //--- Methods---
        public async Task<ModuleInfo> DoAsync(
            string cloudformationFile,
            bool forcePublish,
            string moduleOrigin
        ) {
            _forcePublish = forcePublish;
            _changesDetected = false;

            // make sure there is a deployment bucket
            if(Settings.DeploymentBucketName == null) {
                LogError("missing deployment bucket", new LambdaSharpDeploymentTierSetupException(Settings.TierName));
                return null;
            }

            // load cloudformation template
            if(!File.Exists(cloudformationFile)) {
                LogError("folder does not contain a CloudFormation file for publishing");
                return null;
            }

            // load cloudformation file
            if(!_loader.TryLoadFromFile(cloudformationFile, out var manifest)) {
                return null;
            }

            // update module origin
            var moduleInfo = manifest.ModuleInfo.WithOrigin(moduleOrigin ?? Settings.DeploymentBucketName);
            manifest.ModuleInfo = moduleInfo;

            // check if we want to always publish
            if(!forcePublish) {

                // check if module has a stable version, but is compiled from a dirty git branch
                if(!moduleInfo.Version.IsPreRelease && (manifest.Git.SHA?.StartsWith("DIRTY-") ?? false)) {
                    LogError($"attempting to publish an immutable release of {moduleInfo.FullName} (v{moduleInfo.Version}) with uncommitted/untracked changes; use --force-publish to proceed anyway");
                    return null;
                }

                // check if a manifest already exists for this version
                if(!moduleInfo.Version.IsPreRelease && await Settings.S3Client.DoesS3ObjectExistAsync(Settings.DeploymentBucketName, moduleInfo.VersionPath)) {
                    LogError($"{moduleInfo.FullName} (v{moduleInfo.Version}) is already published; use --force-publish to proceed anyway");
                    return null;
                }
            }

            // publish module
            Console.WriteLine($"Publishing module: {manifest.GetFullName()}");

            // verify that all files referenced by manifest exist (NOTE: source file was already checked)
            foreach(var file in manifest.Assets) {
                var filepath = Path.Combine(Settings.OutputDirectory, file);
                if(!File.Exists(filepath)) {
                    LogError($"could not find: '{filepath}'");
                }
            }
            if(HasErrors) {
                return null;
            }

            // discover module dependencies
            var dependencies = await _loader.DiscoverAllDependenciesAsync(manifest, checkExisting: false);
            if(HasErrors) {
                return null;
            }

            // upload assets
            for(var i = 0; i < manifest.Assets.Count; ++i) {
                await UploadPackageAsync(manifest, manifest.Assets[i], "asset");
            }

            // upload CloudFormation template
            var templateKey = await UploadTemplateFileAsync(manifest, "template");

            // copy all dependencies to deployment bucket that are missing or have a pre-release version
            foreach(var dependency in dependencies.Where(dependency => dependency.ModuleLocation.SourceBucketName != Settings.DeploymentBucketName)) {
                var imported = false;

                // copy check-summed module assets (guaranteed immutable)
                foreach(var asset in dependency.Manifest.Assets) {
                    imported = imported | await CopyS3Object(dependency.ModuleLocation.ModuleInfo.Origin, dependency.ModuleLocation.ModuleInfo.GetAssetPath(asset));
                }

                // copy version manifest
                imported = imported | await CopyS3Object(dependency.ModuleLocation.ModuleInfo.Origin, dependency.ModuleLocation.ModuleInfo.VersionPath, replace: dependency.ModuleLocation.ModuleInfo.Version.IsPreRelease);

                // show message if any assets were imported
                if(imported) {
                    Console.WriteLine($"=> Imported {dependency.ModuleLocation.ModuleInfo.ToModuleReference()}");
                }
            }

            // upload manifest under version number
            if(_changesDetected) {
                var request = new TransferUtilityUploadRequest {
                    InputStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest, new JsonSerializerSettings {
                        Formatting = Formatting.None,
                        NullValueHandling = NullValueHandling.Ignore
                    }))),
                    BucketName = Settings.DeploymentBucketName,
                    ContentType = "application/json",
                    Key = moduleInfo.VersionPath
                };
                request.Metadata[AMAZON_METADATA_ORIGIN] = Settings.DeploymentBucketName;
                await _transferUtility.UploadAsync(request);
            } else {

                // NOTE: this message should never appear since we already do a similar check earlier
                Console.WriteLine($"=> No changes found to upload");
            }
            return manifest.ModuleInfo;
        }

        private async Task<string> UploadTemplateFileAsync(ModuleManifest manifest, string description) {
            var moduleInfo = manifest.ModuleInfo;

            // rewrite assets in manifest to have an absolute path
            manifest.Assets = manifest.Assets
                .OrderBy(asset => asset)
                .Select(asset => moduleInfo.GetAssetPath(asset)).ToList();

            // add template to list of assets
            var destinationKey = manifest.GetModuleTemplatePath();
            manifest.Assets.Insert(0, destinationKey);

            // update cloudformation template with manifest and minify it
            var template = File.ReadAllText(SourceFilename)
                .Replace(ModuleInfo.MODULE_ORIGIN_PLACEHOLDER, moduleInfo.Origin ?? throw new ApplicationException("missing Origin information"));
            var cloudformation = JObject.Parse(template);
            ((JObject)cloudformation["Metadata"])["LambdaSharp::Manifest"] = JObject.FromObject(manifest, new JsonSerializer {
                NullValueHandling = NullValueHandling.Ignore
            });
            var minified = JsonConvert.SerializeObject(cloudformation, new JsonSerializerSettings {
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore
            });

            // upload minified json
            if(_forcePublish || !await DoesS3ObjectExistsAsync(destinationKey)) {
                Console.WriteLine($"=> Uploading {description}: s3://{Settings.DeploymentBucketName}/{destinationKey}");
                var request = new TransferUtilityUploadRequest {
                    InputStream = new MemoryStream(Encoding.UTF8.GetBytes(minified)),
                    BucketName = Settings.DeploymentBucketName,
                    ContentType = "application/json",
                    Key = destinationKey
                };
                request.Metadata[AMAZON_METADATA_ORIGIN] = Settings.DeploymentBucketName;
                await _transferUtility.UploadAsync(request);
                _changesDetected = true;
            }
            return destinationKey;
        }

        private async Task<string> UploadPackageAsync(ModuleManifest manifest, string relativeFilePath, string description) {
            var filePath = Path.Combine(Settings.OutputDirectory, relativeFilePath);

            // only upload files that don't exist
            var destinationKey = manifest.ModuleInfo.GetAssetPath(Path.GetFileName(filePath));
            if(_forcePublish || !await DoesS3ObjectExistsAsync(destinationKey)) {
                Console.WriteLine($"=> Uploading {description}: s3://{Settings.DeploymentBucketName}/{destinationKey}");
                var request = new TransferUtilityUploadRequest {
                    FilePath = filePath,
                    BucketName = Settings.DeploymentBucketName,
                    Key = destinationKey
                };
                request.Metadata[AMAZON_METADATA_ORIGIN] = Settings.DeploymentBucketName;
                await _transferUtility.UploadAsync(request);
                _changesDetected = true;
            }
            return destinationKey;
        }

        private Task<bool> DoesS3ObjectExistsAsync(string key) => Settings.S3Client.DoesS3ObjectExistAsync(Settings.DeploymentBucketName, key);

        private async Task<bool> CopyS3Object(string sourceBucket, string key, bool replace = false) {

            // check if target object already exists
            var found = false;
            try {
                var existing = await Settings.S3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest {
                    BucketName = Settings.DeploymentBucketName,
                    Key = key,
                    RequestPayer = RequestPayer.Requester
                });
                found = true;

                // TODO: should copying the original be forceable?

                // check if this object was uploaded locally and therefore should not be replaced
                if(existing.Metadata[AMAZON_METADATA_ORIGIN] == Settings.DeploymentBucketName) {
                    return false;
                }
            } catch { }
            if(!found || replace) {
                var request = new CopyObjectRequest {
                    SourceBucket = sourceBucket,
                    SourceKey = key,
                    DestinationBucket = Settings.DeploymentBucketName,
                    DestinationKey = key,
                    MetadataDirective = Amazon.S3.S3MetadataDirective.COPY,
                    RequestPayer = RequestPayer.Requester
                };

                // capture the origin of this object
                request.Metadata[AMAZON_METADATA_ORIGIN] = sourceBucket;
                await Settings.S3Client.CopyObjectAsync(request);
                 _changesDetected = true;
                return true;
           }
           return false;
        }
    }
}