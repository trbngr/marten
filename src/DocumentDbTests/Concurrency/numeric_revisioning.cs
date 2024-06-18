using System;
using System.Threading.Tasks;
using Castle.Components.DictionaryAdapter;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Marten.Exceptions;
using Marten.Metadata;
using Marten.Schema;
using Marten.Services;
using Marten.Testing.Documents;
using Marten.Testing.Harness;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace DocumentDbTests.Concurrency;

public class numeric_revisioning: OneOffConfigurationsContext
{
    private readonly ITestOutputHelper _output;

    public numeric_revisioning(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void use_numeric_revisions_is_off_by_default()
    {
        var mapping = (DocumentMapping)theStore.Options.Storage.FindMapping(typeof(Target));
        mapping.UseNumericRevisions.ShouldBeFalse();
        mapping.Metadata.Revision.Enabled.ShouldBeFalse();
    }


    [Fact]
    public void using_fluent_interface()
    {
        StoreOptions(opts => opts.Schema.For<Target>().UseNumericRevisions(true));

        var mapping = (DocumentMapping)theStore.Options.Storage.FindMapping(typeof(Target));
        mapping.Metadata.Revision.Enabled.ShouldBeTrue();
        mapping.UseNumericRevisions.ShouldBeTrue();
    }

    [Fact]
    public void decorate_int_property_with_Version_attribute()
    {
        var mapping = (DocumentMapping)theStore.Options.Storage.FindMapping(typeof(OtherRevisionedDoc));
        mapping.Metadata.Revision.Enabled.ShouldBeTrue();
        mapping.UseNumericRevisions.ShouldBeTrue();
        mapping.Metadata.Revision.Member.Name.ShouldBe("Version");
    }

    [Fact]
    public void infer_configuration_from_IRevisioned_interface()
    {
        var mapping = (DocumentMapping)theStore.Options.Storage.FindMapping(typeof(RevisionedDoc));
        mapping.Metadata.Revision.Enabled.ShouldBeTrue();
        mapping.UseNumericRevisions.ShouldBeTrue();
        mapping.Metadata.Revision.Member.Name.ShouldBe("Version");
    }

    [Fact]
    public void use_mapped_property_for_numeric_versioning()
    {
        using var store = SeparateStore(_ =>
        {
            _.Schema.For<UnconventionallyVersionedDoc>().UseNumericRevisions(true).Metadata(m =>
            {
                m.Revision.MapTo(x => x.UnconventionalVersion);
            });
        });
        store.StorageFeatures.MappingFor(typeof(UnconventionallyVersionedDoc))
            .Metadata.Revision.Member.Name.ShouldBe(nameof(UnconventionallyVersionedDoc.UnconventionalVersion));

        var session = store.LightweightSession();
        var doc = new UnconventionallyVersionedDoc{Id = Guid.NewGuid(), Name = "Initial Name"};

        session.Insert(doc);
        session.SaveChanges();

        var loaded = session.Load<UnconventionallyVersionedDoc>(doc.Id);
        loaded.UnconventionalVersion.ShouldBe(1);

        doc.Name = "New Name";

        session.Store(doc);
        session.SaveChanges();

        loaded = session.Load<UnconventionallyVersionedDoc>(doc.Id);
        loaded.UnconventionalVersion.ShouldBe(2);
    }

    [Fact]
    public async Task happy_path_insert()
    {
        var doc = new RevisionedDoc { Name = "Tim" };
        theSession.Insert(doc);
        theSession.SaveChanges();

        var loaded = await theSession.LoadAsync<RevisionedDoc>(doc.Id);
        loaded.Version.ShouldBe(1);

        doc.Version.ShouldBe(1);
    }

    [Fact]
    public async Task fetch_document_metadata()
    {
        var doc = new RevisionedDoc { Name = "Tim" };
        theSession.Insert(doc);
        theSession.SaveChanges();

        var metadata = await theSession.MetadataForAsync(doc);
        metadata.CurrentRevision.ShouldBe(1);
    }

    [Fact]
    public async Task bulk_inserts()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        var doc2 = new RevisionedDoc { Name = "Molly" };
        var doc3 = new RevisionedDoc { Name = "JD" };

        await theStore.BulkInsertDocumentsAsync(new[] { doc1, doc2, doc3 });

        (await theSession.MetadataForAsync(doc1)).CurrentRevision.ShouldBe(1);
        (await theSession.MetadataForAsync(doc2)).CurrentRevision.ShouldBe(1);
        (await theSession.MetadataForAsync(doc3)).CurrentRevision.ShouldBe(1);

        (await theSession.LoadAsync<RevisionedDoc>(doc1.Id)).ShouldNotBeNull();
        (await theSession.LoadAsync<RevisionedDoc>(doc2.Id)).ShouldNotBeNull();
        (await theSession.LoadAsync<RevisionedDoc>(doc3.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task store_with_no_revision_from_start_succeeds_with_revision_1()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        (await theSession.MetadataForAsync(doc1)).CurrentRevision.ShouldBe(1);
        (await theSession.LoadAsync<RevisionedDoc>(doc1.Id)).Version.ShouldBe(1);
    }

    [Fact]
    public async Task store_twice_with_no_version_can_override()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();


        theSession.Logger = new TestOutputMartenLogger(_output);
        theSession.Store(new RevisionedDoc{Id = doc1.Id, Name = "Brad"});
        theSession.SaveChanges();

        (await theSession.LoadAsync<RevisionedDoc>(doc1.Id)).Name.ShouldBe("Brad");
    }

    [Fact]
    public async Task each_store_should_increase_the_version()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        await theSession.SaveChangesAsync();
        doc1.Version.ShouldBe(1);

        doc1.Name = "Brad";
        theSession.Store(doc1);
        await theSession.SaveChangesAsync();
        doc1.Version.ShouldBe(2);

        doc1.Name = "Janet";
        theSession.Store(doc1);

        // It's going to warn you to use UpdateRevision here.
        var ex = await Should.ThrowAsync<ConcurrencyException>(async () =>
        {
            await theSession.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task optimistic_concurrency_failure_with_update_revision()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        theSession.Store(doc1);
        theSession.SaveChanges();

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Wrong" };
        theSession.UpdateRevision(doc2, doc1.Version + 1);
        theSession.SaveChanges();

        theSession.Logger = new TestOutputMartenLogger(_output);

        await Should.ThrowAsync<ConcurrencyException>(async () =>
        {
            theSession.UpdateRevision(doc2, 2);
            theSession.SaveChanges();
        });
    }

    [Fact]
    public async Task optimistic_concurrency_miss_with_try_update_revision()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        theSession.Store(doc1);
        theSession.SaveChanges();

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Tron" };
        theSession.UpdateRevision(doc2, doc1.Version + 1);
        theSession.SaveChanges();

        // No failure
        theSession.TryUpdateRevision(doc2, 2);
        theSession.SaveChanges();

        (await theSession.LoadAsync<RevisionedDoc>(doc1.Id)).Name.ShouldBe("Tron");
    }

    [Fact]
    public async Task update_just_overwrites_and_increments_version()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        doc1.Version = 0;
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        doc1.Version = 0;
        theSession.Store(doc1);
        theSession.SaveChanges();

        theSession.Logger = new TestOutputMartenLogger(_output);

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Wrong", Version = 0};
        theSession.UpdateRevision(doc2, doc1.Version + 1);
        theSession.SaveChanges();

        doc2.Name = "Last";
        doc2.Version = 0;
        theSession.Update(doc2);
        theSession.SaveChanges();

        var doc3 = await theSession.LoadAsync<RevisionedDoc>(doc1.Id);
        doc2.Version = 0;
        doc3.Name.ShouldBe("Last");
        doc3.Version.ShouldBe(5);

        (await theSession.MetadataForAsync(doc1)).CurrentRevision.ShouldBe(5);


    }

    [Fact]
    public async Task update_revision_and_jumping_multiples()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        theSession.Store(doc1);
        theSession.SaveChanges();

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Tron", Version = 2};
        theSession.UpdateRevision(doc2, 10);
        theSession.SaveChanges();

        (await theSession.LoadAsync<RevisionedDoc>(doc1.Id)).Name.ShouldBe("Tron");
        (await theSession.MetadataForAsync(doc2)).CurrentRevision.ShouldBe(10);
    }

    [Fact]
    public async Task overwrite_increments_version()
    {
        theSession.Logger = new TestOutputMartenLogger(_output);

        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        doc1.Version = 0;
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        doc1.Version = 0;
        theSession.Store(doc1);
        theSession.SaveChanges();

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Wrong", Version = 2};
        theSession.UpdateRevision(doc2, doc1.Version + 1);
        theSession.SaveChanges();

        using var session2 =
            theStore.OpenSession(new SessionOptions { ConcurrencyChecks = ConcurrencyChecks.Disabled });


        session2.Logger = new TestOutputMartenLogger(_output);

        doc2.Name = "Last";
        doc2.Version = 0;
        session2.Store(doc2);
        await session2.SaveChangesAsync();

        var doc3 = await session2.LoadAsync<RevisionedDoc>(doc1.Id);
        doc3.Name.ShouldBe("Last");
        doc3.Version.ShouldBe(5);

        (await session2.MetadataForAsync(doc1)).CurrentRevision.ShouldBe(5);


    }



    /********** START SYNC *******************/

    [Fact]
    public void happy_path_insert_sync()
    {
        var doc = new RevisionedDoc { Name = "Tim" };
        theSession.Insert(doc);
        theSession.SaveChanges();

        var loaded = theSession.Load<RevisionedDoc>(doc.Id);
        loaded.Version.ShouldBe(1);

        doc.Version.ShouldBe(1);
    }

    [Fact]
    public void fetch_document_metadata_sync()
    {
        var doc = new RevisionedDoc { Name = "Tim" };
        theSession.Insert(doc);
        theSession.SaveChanges();

        var metadata = theSession.MetadataFor(doc);
        metadata.CurrentRevision.ShouldBe(1);
    }

    [Fact]
    public void bulk_inserts_sync()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        var doc2 = new RevisionedDoc { Name = "Molly" };
        var doc3 = new RevisionedDoc { Name = "JD" };

        theStore.BulkInsertDocuments(new[] { doc1, doc2, doc3 });

        (theSession.MetadataFor(doc1)).CurrentRevision.ShouldBe(1);
        (theSession.MetadataFor(doc2)).CurrentRevision.ShouldBe(1);
        (theSession.MetadataFor(doc3)).CurrentRevision.ShouldBe(1);

        (theSession.Load<RevisionedDoc>(doc1.Id)).ShouldNotBeNull();
        (theSession.Load<RevisionedDoc>(doc2.Id)).ShouldNotBeNull();
        (theSession.Load<RevisionedDoc>(doc3.Id)).ShouldNotBeNull();
    }

    [Fact]
    public void store_with_no_revision_from_start_succeeds_with_revision_1_sync()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        (theSession.MetadataFor(doc1)).CurrentRevision.ShouldBe(1);
        (theSession.Load<RevisionedDoc>(doc1.Id)).Version.ShouldBe(1);
    }

    [Fact]
    public void store_twice_with_no_version_can_override_sync()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();


        theSession.Logger = new TestOutputMartenLogger(_output);
        theSession.Store(new RevisionedDoc{Id = doc1.Id, Name = "Brad"});
        theSession.SaveChanges();

        (theSession.Load<RevisionedDoc>(doc1.Id)).Name.ShouldBe("Brad");
    }

    [Fact]
    public void optimistic_concurrency_failure_with_update_revision_sync()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        theSession.Store(doc1);
        theSession.SaveChanges();

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Wrong" };
        theSession.UpdateRevision(doc2, doc1.Version + 1);
        theSession.SaveChanges();

        Should.Throw<ConcurrencyException>(async () =>
        {
            theSession.UpdateRevision(doc2, 2);
            theSession.SaveChanges();
        });
    }

    [Fact]
    public void optimistic_concurrency_miss_with_try_update_revision_sync()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        theSession.Store(doc1);
        theSession.SaveChanges();

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Tron" };
        theSession.UpdateRevision(doc2, doc1.Version + 1);
        theSession.SaveChanges();

        // No failure
        theSession.TryUpdateRevision(doc2, 2);
        theSession.SaveChanges();

        (theSession.Load<RevisionedDoc>(doc1.Id)).Name.ShouldBe("Tron");
    }

    [Fact]
    public void update_just_overwrites_and_increments_version_sync()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        doc1.Version = 0;
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        doc1.Version = 0;
        theSession.Store(doc1);
        theSession.SaveChanges();

        theSession.Logger = new TestOutputMartenLogger(_output);

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Wrong", Version = 0};
        theSession.UpdateRevision(doc2, doc1.Version + 1);
        theSession.SaveChanges();

        doc2.Name = "Last";
        doc2.Version = 0;
        theSession.Update(doc2);
        theSession.SaveChanges();

        var doc3 = theSession.Load<RevisionedDoc>(doc1.Id);
        doc2.Version = 0;
        doc3.Name.ShouldBe("Last");
        doc3.Version.ShouldBe(5);

        (theSession.MetadataFor(doc1)).CurrentRevision.ShouldBe(5);
    }

    [Fact]
    public void update_revision_and_jumping_multiples_sync()
    {
        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        theSession.Store(doc1);
        theSession.SaveChanges();

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Tron", Version = 2};
        theSession.UpdateRevision(doc2, 10);
        theSession.SaveChanges();

        (theSession.Load<RevisionedDoc>(doc1.Id)).Name.ShouldBe("Tron");
        (theSession.MetadataFor(doc2)).CurrentRevision.ShouldBe(10);
    }

    [Fact]
    public void overwrite_increments_version_sync()
    {
        theSession.Logger = new TestOutputMartenLogger(_output);

        var doc1 = new RevisionedDoc { Name = "Tim" };
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Bill";
        doc1.Version = 0;
        theSession.Store(doc1);
        theSession.SaveChanges();

        doc1.Name = "Dru";
        doc1.Version = 0;
        theSession.Store(doc1);
        theSession.SaveChanges();

        var doc2 = new RevisionedDoc { Id = doc1.Id, Name = "Wrong", Version = 2};
        theSession.UpdateRevision(doc2, doc1.Version + 1);
        theSession.SaveChanges();

        using var session2 =
            theStore.OpenSession(new SessionOptions { ConcurrencyChecks = ConcurrencyChecks.Disabled });


        session2.Logger = new TestOutputMartenLogger(_output);

        doc2.Name = "Last";
        doc2.Version = 0;
        session2.Store(doc2);
        session2.SaveChanges();

        var doc3 = session2.Load<RevisionedDoc>(doc1.Id);
        doc3.Name.ShouldBe("Last");
        doc3.Version.ShouldBe(5);

        (session2.MetadataFor(doc1)).CurrentRevision.ShouldBe(5);

    }



}



public class RevisionedDoc: IRevisioned
{
    public Guid Id { get; set; }
    public string Name { get; set; }

    public int Version { get; set; }
}

public class OtherRevisionedDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; }

    [Version]
    public int Version { get; set; }
}

public class UnconventionallyVersionedDoc
{
    public Guid Id { get; set; }

    public string Name { get; set; }

    public int UnconventionalVersion { get; set; }
}
